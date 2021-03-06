#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Graphics
{
	public sealed class WorldRenderer : IDisposable
	{
		public static readonly Func<IRenderable, int> RenderableScreenZPositionComparisonKey =
			r => ZPosition(r.Pos, r.ZOffset);

		public readonly World World;
		public readonly Theater Theater;
		public Viewport Viewport { get; private set; }

		public event Action PaletteInvalidated = null;

		readonly HardwarePalette palette = new HardwarePalette();
		readonly Dictionary<string, PaletteReference> palettes = new Dictionary<string, PaletteReference>();
		readonly TerrainRenderer terrainRenderer;
		readonly Lazy<DeveloperMode> devTrait;
		readonly Func<string, PaletteReference> createPaletteReference;

		internal WorldRenderer(World world)
		{
			World = world;
			Viewport = new Viewport(this, world.Map);

			createPaletteReference = CreatePaletteReference;

			foreach (var pal in world.TraitDict.ActorsWithTrait<ILoadsPalettes>())
				pal.Trait.LoadPalettes(this);

			foreach (var p in world.Players)
				UpdatePalettesForPlayer(p.InternalName, p.Color, false);

			palette.Initialize();

			Theater = new Theater(world.TileSet);
			terrainRenderer = new TerrainRenderer(world, this);

			devTrait = Exts.Lazy(() => world.LocalPlayer != null ? world.LocalPlayer.PlayerActor.Trait<DeveloperMode>() : null);
		}

		public void UpdatePalettesForPlayer(string internalName, HSLColor color, bool replaceExisting)
		{
			foreach (var pal in World.WorldActor.TraitsImplementing<ILoadsPlayerPalettes>())
				pal.LoadPlayerPalettes(this, internalName, color, replaceExisting);
		}

		PaletteReference CreatePaletteReference(string name)
		{
			var pal = palette.GetPalette(name);
			return new PaletteReference(name, palette.GetPaletteIndex(name), pal, palette);
		}

		public PaletteReference Palette(string name) { return palettes.GetOrAdd(name, createPaletteReference); }
		public void AddPalette(string name, ImmutablePalette pal, bool allowModifiers = false, bool allowOverwrite = false)
		{
			if (allowOverwrite && palette.Contains(name))
				ReplacePalette(name, pal);
			else
			{
				var oldHeight = palette.Height;
				palette.AddPalette(name, pal, allowModifiers);

				if (oldHeight != palette.Height && PaletteInvalidated != null)
					PaletteInvalidated();
			}
		}

		public void ReplacePalette(string name, IPalette pal)
		{
			palette.ReplacePalette(name, pal);

			// Update cached PlayerReference if one exists
			if (palettes.ContainsKey(name))
				palettes[name].Palette = pal;
		}

		List<IFinalizedRenderable> GenerateRenderables()
		{
			var actors = World.ScreenMap.ActorsInBox(Viewport.TopLeft, Viewport.BottomRight)
				.Append(World.WorldActor);

			// Include player actor for the rendered player
			if (World.RenderPlayer != null)
				actors = actors.Append(World.RenderPlayer.PlayerActor);

			var worldRenderables = actors.SelectMany(a => a.Render(this));
			if (World.OrderGenerator != null)
				worldRenderables = worldRenderables.Concat(World.OrderGenerator.Render(this, World));

			worldRenderables = worldRenderables.OrderBy(RenderableScreenZPositionComparisonKey);

			// Effects are drawn on top of all actors
			// HACK: Effects aren't interleaved with actors.
			var effectRenderables = World.Effects
				.SelectMany(e => e.Render(this));

			if (World.OrderGenerator != null)
				effectRenderables = effectRenderables.Concat(World.OrderGenerator.RenderAfterWorld(this, World));

			Game.Renderer.WorldVoxelRenderer.BeginFrame();
			var renderables = worldRenderables.Concat(effectRenderables)
				.Select(r => r.PrepareRender(this)).ToList();
			Game.Renderer.WorldVoxelRenderer.EndFrame();

			return renderables;
		}

		public void Draw()
		{
			RefreshPalette();

			if (World.Type == WorldType.Shellmap && !Game.Settings.Game.ShowShellmap)
				return;

			var renderables = GenerateRenderables();
			var bounds = Viewport.ScissorBounds;
			Game.Renderer.EnableScissor(bounds);

			terrainRenderer.Draw(this, Viewport);
			Game.Renderer.Flush();

			for (var i = 0; i < renderables.Count; i++)
				renderables[i].Render(this);

			// added for contrails
			foreach (var a in World.ActorsWithTrait<IPostRender>())
				if (a.Actor.IsInWorld && !a.Actor.Destroyed)
					a.Trait.RenderAfterWorld(this, a.Actor);

			var renderShroud = World.RenderPlayer != null ? World.RenderPlayer.Shroud : null;

			foreach (var a in World.ActorsWithTrait<IRenderShroud>())
				a.Trait.RenderShroud(this, renderShroud);

			if (devTrait.Value != null && devTrait.Value.ShowDebugGeometry)
				for (var i = 0; i < renderables.Count; i++)
					renderables[i].RenderDebugGeometry(this);

			Game.Renderer.DisableScissor();

			var overlayRenderables = World.Selection.Actors.Where(a => !a.Destroyed)
				.SelectMany(a => a.TraitsImplementing<IPostRenderSelection>())
				.SelectMany(t => t.RenderAfterWorld(this));

			Game.Renderer.WorldVoxelRenderer.BeginFrame();
			var finalOverlayRenderables = overlayRenderables.Select(r => r.PrepareRender(this));
			Game.Renderer.WorldVoxelRenderer.EndFrame();

			// HACK: Keep old grouping behaviour
			foreach (var g in finalOverlayRenderables.GroupBy(prs => prs.GetType()))
				foreach (var r in g)
					r.Render(this);

			if (devTrait.Value != null && devTrait.Value.ShowDebugGeometry)
				foreach (var g in finalOverlayRenderables.GroupBy(prs => prs.GetType()))
					foreach (var r in g)
						r.RenderDebugGeometry(this);

			if (World.Type == WorldType.Regular && Game.Settings.Game.AlwaysShowStatusBars)
			{
				foreach (var g in World.Actors.Where(a => !a.Destroyed
					&& a.HasTrait<Selectable>()
					&& !World.FogObscures(a)
					&& !World.Selection.Actors.Contains(a)))

					DrawRollover(g);
			}

			Game.Renderer.Flush();
		}

		public void DrawRollover(Actor unit)
		{
			var selectable = unit.TraitOrDefault<Selectable>();
			if (selectable != null)
			{
				if (selectable.Info.Selectable)
					new SelectionBarsRenderable(unit).Render(this);
			}
		}

		public void DrawRangeCircle(WPos pos, WRange range, Color c)
		{
			var offset = new WVec(range.Range, 0, 0);
			for (var i = 0; i < 32; i++)
			{
				var pa = pos + offset.Rotate(WRot.FromFacing(8 * i));
				var pb = pos + offset.Rotate(WRot.FromFacing(8 * i + 6));
				Game.Renderer.WorldLineRenderer.DrawLine(ScreenPosition(pa), ScreenPosition(pb), c, c);
			}
		}

		public void DrawTargetMarker(Color c, float2 location)
		{
			var tl = new float2(-1 / Viewport.Zoom, -1 / Viewport.Zoom);
			var br = new float2(1 / Viewport.Zoom, 1 / Viewport.Zoom);
			var bl = new float2(tl.X, br.Y);
			var tr = new float2(br.X, tl.Y);

			var wlr = Game.Renderer.WorldLineRenderer;
			wlr.DrawLine(location + tl, location + tr, c, c);
			wlr.DrawLine(location + tr, location + br, c, c);
			wlr.DrawLine(location + br, location + bl, c, c);
			wlr.DrawLine(location + bl, location + tl, c, c);
		}

		public void RefreshPalette()
		{
			palette.ApplyModifiers(World.WorldActor.TraitsImplementing<IPaletteModifier>());
			Game.Renderer.SetPalette(palette);
		}

		// Conversion between world and screen coordinates
		public float2 ScreenPosition(WPos pos)
		{
			var ts = Game.ModData.Manifest.TileSize;
			return new float2(ts.Width * pos.X / 1024f, ts.Height * (pos.Y - pos.Z) / 1024f);
		}

		public int2 ScreenPxPosition(WPos pos)
		{
			// Round to nearest pixel
			var px = ScreenPosition(pos);
			return new int2((int)Math.Round(px.X), (int)Math.Round(px.Y));
		}

		// For scaling vectors to pixel sizes in the voxel renderer
		public float[] ScreenVector(WVec vec)
		{
			var ts = Game.ModData.Manifest.TileSize;
			return new float[] { ts.Width * vec.X / 1024f, ts.Height * vec.Y / 1024f, ts.Height * vec.Z / 1024f, 1 };
		}

		public int2 ScreenPxOffset(WVec vec)
		{
			// Round to nearest pixel
			var px = ScreenVector(vec);
			return new int2((int)Math.Round(px[0]), (int)Math.Round(px[1] - px[2]));
		}

		public float ScreenZPosition(WPos pos, int offset)
		{
			var ts = Game.ModData.Manifest.TileSize;
			return ZPosition(pos, offset) * ts.Height / 1024f;
		}

		static int ZPosition(WPos pos, int offset)
		{
			return pos.Y + pos.Z + offset;
		}

		public WPos Position(int2 screenPx)
		{
			var ts = Game.ModData.Manifest.TileSize;
			return new WPos(1024 * screenPx.X / ts.Width, 1024 * screenPx.Y / ts.Height, 0);
		}

		public void Dispose()
		{
			palette.Dispose();
			Theater.Dispose();
			terrainRenderer.Dispose();
		}
	}
}

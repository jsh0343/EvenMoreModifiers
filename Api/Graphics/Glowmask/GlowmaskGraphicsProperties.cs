using Loot.Api.Builder;

namespace Loot.Api.Graphics.Glowmask
{
	public class GlowmaskGraphicsProperties : GraphicsProperties
	{
		public new static GlowmaskGraphicsPropertiesBuilder Builder => new GlowmaskGraphicsPropertiesBuilder();

		public bool SkipDrawingGlowmask { get; set; }

		public class GlowmaskGraphicsPropertiesBuilder : PropertyBuilder<GlowmaskGraphicsProperties>
		{
			protected override GlowmaskGraphicsProperties DefaultProperty
			{
				set
				{
					Property.SkipDrawing = value.SkipDrawing;
					Property.SkipDrawingGlowmask = value.SkipDrawingGlowmask;
				}
			}

			public GlowmaskGraphicsPropertiesBuilder SkipDrawing(bool value)
			{
				Property.SkipDrawing = value;
				return this;
			}

			public GlowmaskGraphicsPropertiesBuilder SkipDrawingGlowmask(bool val)
			{
				Property.SkipDrawingGlowmask = val;
				return this;
			}
		}
	}
}

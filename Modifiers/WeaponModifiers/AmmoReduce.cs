﻿using System;
using Loot.System;
using Microsoft.Xna.Framework;
using Terraria;

namespace Loot.Modifiers.WeaponModifiers
{
	public class AmmoReduce : WeaponModifier
	{
		public override ModifierEffectTooltipLine[] Description => new[]
			{
				new ModifierEffectTooltipLine { Text = $"{(int)Math.Round(Power)}% chance to not consume ammo", Color = Color.Lime}
			};

		public override float MinMagnitude => 0.05f;
		public override float MaxMagnitude => 1.0f;
		public override float BasePower => 20f;

		public override bool CanRoll(ModifierContext ctx)
		{
			// Only apply on items that consume ammo
			return base.CanRoll(ctx) && ctx.Item.useAmmo > 0;
		}

		public override bool ConsumeAmmo(Item item, Player player)
		{
			return Main.rand.NextFloat() > Power / 100;
		}
	}
}
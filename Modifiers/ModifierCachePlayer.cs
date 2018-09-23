using CheatSheet;
using Loot.Core;
using Loot.Core.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace Loot.Modifiers
{
	public class AutoDelegationEntry
	{
		public Item Item { get; set; }
		public Modifier Modifier { get; set; }

		public AutoDelegationEntry(Item item, Modifier modifier)
		{
			Item = item;
			Modifier = modifier;
		}
	}

	public class OrderedDelegationEntry
	{
		public MethodInfo MethodInfo { get; set; }
		public ModifierEffect Effect { get; set; }
	}

	/*
	 * Do note that this class is very important
	 * And without this class, any of the delegations will not work at all
	 * This class automatically delegates only the applicable modifiers and their effects
	 * \\WHEN NEEDED\\. This means only 'active' items are being updated, that means any held item
	 * and items that are equipped (armor/accessory)
	 */

	/// <summary>
	/// The following code caches the item held and items equipped by players
	/// When equips and held items change, their respective modifiers' effects are automatically
	/// called to detach and attach their delegations
	/// </summary>
	// ReSharper disable once ClassNeverInstantiated.Global
	public sealed class ModifierCachePlayer : ModPlayer
	{
		private bool IsMouseUsable(Item item) => item.damage > 0;

		private Item _oldHeldItem;
		private Item _oldMouseItem;
		private Item[] _oldEquips;
		private Item[] _oldVanityEquips;
		private bool _forceEquipUpdate;
		private Item[] _oldCheatSheetEquips;
		internal bool Ready;
		private List<Type> _modifierEffects;
		private List<AutoDelegationEntry> _detachList;
		private List<AutoDelegationEntry> _attachList;

		public override void Initialize()
		{
			_oldHeldItem = null;
			_oldMouseItem = null;
			_oldEquips = new Item[8];
			_oldVanityEquips = new Item[8];
			_forceEquipUpdate = false;
			_oldCheatSheetEquips = new Item[6]; // MaxExtraAccessories = 6
			_modifierEffects = new List<Type>();
			_detachList = new List<AutoDelegationEntry>();
			_attachList = new List<AutoDelegationEntry>();
			Ready = false;
		}

		/// <summary>
		/// This method will return a list of <see cref="ModifierEffect"/>s based on <see cref="Modifier"/>s passed to it 
		/// </summary>
		private List<ModifierEffect> GetModifierEffectsForDelegations(List<AutoDelegationEntry> list, ModifierPlayer modPlayer, Func<ModifierEffect, bool> conditionFunc)
		{
			var tempList = new List<ModifierEffect>();
			foreach (var delegationTuple in list)
			{
				var effectsAttribute =
					delegationTuple.Modifier
						.GetType()
						.GetCustomAttribute<UsesEffectAttribute>();

				if (effectsAttribute != null)
				{
					foreach (Type effect in effectsAttribute.Effects)
					{
						var modEffect = modPlayer.GetEffect(effect);
						if (modEffect != null && conditionFunc.Invoke(modEffect))
						{
							tempList.Add(modEffect);
						}
					}
				}
			}

			return tempList;
		}

		/// <summary>
		/// This method handles updating all delegations
		/// First, it will perform 'manual' detachments calling <see cref="ModifierEffect.DetachDelegations(ModifierPlayer)"/>
		/// then it will perform 'manual' attachments calling <see cref="ModifierEffect.AttachDelegations(ModifierPlayer)"/>
		/// These will unbind/bind <see cref="ModifierEffect.ResetEffects"/> to <see cref="ModifierPlayer.OnResetEffects"/>
		/// and set <see cref="ModifierEffect.IsBeingDelegated"/> to false or true.
		/// 
		/// After this, the automatic detachments/attachments are done which will bind any methods
		/// to whichever method noted in their <see cref="AutoDelegation"/> attribute.
		/// </summary>
		private void UpdateAttachments()
		{
			ModifierPlayer modplr = ModifierPlayer.Player(player);

			// Manual detach
			var detachEffects = GetModifierEffectsForDelegations(_detachList, modplr, (e) => e.IsBeingDelegated && !_modifierEffects.Contains(e.GetType()));
			var attachEffects = GetModifierEffectsForDelegations(_attachList, modplr, (e) => !e.IsBeingDelegated);
			// Automatic delegation lists
			var orderedDetachList = OrderDelegationList(_detachList, modplr)
				.Where(x => x.Effect.IsBeingDelegated && !_modifierEffects.Contains(x.Effect.GetType()))
				.GroupBy(x => x.MethodInfo)
				.Select(x => x.First())
				.ToList();

			var orderedAttachList = OrderDelegationList(_attachList, modplr)
				.Where(x => !x.Effect.IsBeingDelegated)
				.GroupBy(x => x.MethodInfo)
				.Select(x => x.First())
				.ToList();

			// Manual detach
			foreach (var effect in detachEffects.Distinct())
			{
				modplr.OnResetEffects -= effect.ResetEffects;
				effect._DetachDelegations(modplr);
				effect.IsBeingDelegated = false;
			}

			// Manual attach
			foreach (var effect in attachEffects.Distinct())
			{
				modplr.OnResetEffects += effect.ResetEffects;
				effect.AttachDelegations(modplr);
				effect.IsBeingDelegated = true;
			}

			// Auto delegation detach
			foreach (var info in orderedDetachList)
			{
				var attr = info.MethodInfo.GetCustomAttribute<AutoDelegation>();
				attr.Detach(modplr, info.MethodInfo, info.Effect);
			}

			// Auto delegation attach
			foreach (var info in orderedAttachList)
			{
				var attr = info.MethodInfo.GetCustomAttribute<AutoDelegation>();
				attr.Attach(modplr, info.MethodInfo, info.Effect);
			}
		}

		/// <summary>
		/// This method orders a list of <see cref="AutoDelegationEntry"/> into a list of <see cref="OrderedDelegationEntry"/>
		/// following the rules of <see cref="DelegationPrioritizationAttribute"/> which orders by Early/Late prioritization
		/// and a level ranging from 0-999 which indicates how much that prioritization should be enforced.
		/// </summary>
		private List<OrderedDelegationEntry> OrderDelegationList(List<AutoDelegationEntry> list, ModifierPlayer modPlayer)
		{
			var tempList = new List<OrderedDelegationEntry>();
			var tempEarlyMethods = new List<OrderedDelegationEntry>();
			var tempMiddleMethods = new List<OrderedDelegationEntry>();
			var tempLateMethods = new List<OrderedDelegationEntry>();

			var effects = GetModifierEffectsForDelegations(list, modPlayer, (e) => true);

			foreach (var modEffect in effects)
			{
				var delegatedMethods = modEffect
					.GetType()
					.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
					.Where(x => x.GetCustomAttributes(typeof(AutoDelegation), false).Length > 0)
					.ToArray();

				tempEarlyMethods.AddRange(delegatedMethods.Where(x =>
				{
					var attr = x.GetCustomAttribute(typeof(DelegationPrioritizationAttribute));
					if (attr != null && (attr as DelegationPrioritizationAttribute).DelegationPrioritization == DelegationPrioritization.Early)
					{
						return true;
					}

					return false;
				}).Select(x => new OrderedDelegationEntry
				{
					MethodInfo = x,
					Effect = modEffect
				}));

				tempMiddleMethods.AddRange(delegatedMethods.Where(x =>
				{
					var attr = x.GetCustomAttribute(typeof(DelegationPrioritizationAttribute));
					return attr == null;
				}).Select(x => new OrderedDelegationEntry
				{
					MethodInfo = x,
					Effect = modEffect
				}));

				tempLateMethods.AddRange(delegatedMethods.Where(x =>
				{
					var attr = x.GetCustomAttribute(typeof(DelegationPrioritizationAttribute));
					if (attr != null && (attr as DelegationPrioritizationAttribute).DelegationPrioritization == DelegationPrioritization.Late)
					{
						return true;
					}

					return false;
				}).Select(x => new OrderedDelegationEntry
				{
					MethodInfo = x,
					Effect = modEffect
				}));
			}

			tempEarlyMethods = tempEarlyMethods.OrderByDescending(x =>
			{
				var attr = x.MethodInfo.GetCustomAttribute(typeof(DelegationPrioritizationAttribute));
				return ((DelegationPrioritizationAttribute)attr).DelegationLevel;
			}).ToList();

			tempLateMethods = tempLateMethods.OrderByDescending(x =>
			{
				var attr = x.MethodInfo.GetCustomAttribute(typeof(DelegationPrioritizationAttribute));
				return ((DelegationPrioritizationAttribute)attr).DelegationLevel;
			}).ToList();

			tempList.AddRange(tempEarlyMethods);
			tempList.AddRange(tempMiddleMethods);
			tempList.AddRange(tempLateMethods);
			return tempList;
		}

		public override void PostUpdate()
		{
			// If our equips cache is not the right size we resize it and force an update
			if (_oldEquips.Length != 8 + player.extraAccessorySlots)
			{
				Ready = false;
				_forceEquipUpdate = true;
				Array.Resize(ref _oldEquips, 8 + player.extraAccessorySlots);
			}

			if (_oldVanityEquips.Length != 8 + player.extraAccessorySlots)
			{
				Ready = false;
				_forceEquipUpdate = true;
				Array.Resize(ref _oldVanityEquips, 8 + player.extraAccessorySlots);
			}

			_detachList.Clear();
			_attachList.Clear();

			CacheModifierEffects(player);

			bool flag = UpdateMouseItemCache();
			if (flag) UpdateHeldItemCache();
			UpdateEquipsCache();
			if (Loot.CheatSheetLoaded)
			{
				UpdateCheatSheetCache();
			}
			UpdateVanityCache();

			UpdateAttachments();

			_forceEquipUpdate = false;
			Ready = true;
		}

		private void AddDetachItem(Item item, Modifier modifier)
		{
			ActivatedModifierItem.Item(item).IsActivated = false;
			_detachList.Add(new AutoDelegationEntry(item, modifier));
		}

		private void AddAttachItem(Item item, Modifier modifier)
		{
			ActivatedModifierItem.Item(item).IsActivated = true;
			_attachList.Add(new AutoDelegationEntry(item, modifier));
		}

		private void UpdateVanityCache()
		{
			// vanity
			for (int i = 13; i < 18 + player.extraAccessorySlots; i++)
			{
				var oldEquip = _oldVanityEquips[i - 13];
				var newEquip = player.armor[i];

				// If equip slot needs an update
				if (_forceEquipUpdate || oldEquip == null || newEquip.IsNotTheSameAs(oldEquip))
				{
					Ready = false;

					// detach old first
					if (oldEquip != null && !oldEquip.IsAir && ActivatedModifierItem.Item(oldEquip).IsVanityActivated)
					{
						foreach (Modifier m in EMMItem.GetActivePool(oldEquip))
						{
							AddDetachItem(oldEquip, m);
						}
					}

					// attach new
					if (newEquip != null && !newEquip.IsAir && ActivatedModifierItem.Item(newEquip).IsVanityActivated)
					{
						foreach (Modifier m in EMMItem.GetActivePool(newEquip))
						{
							AddAttachItem(newEquip, m);
						}
					}

					_oldVanityEquips[i - 13] = newEquip;
				}
			}
		}

		private void UpdateHeldItemCache()
		{
			// If held item needs an update
			if (_oldHeldItem == null || _oldHeldItem.IsNotTheSameAs(player.HeldItem))
			{
				Ready = false;

				// detach old held item
				if (_oldHeldItem != null && !_oldHeldItem.IsAir && IsMouseUsable(_oldHeldItem))
				{
					foreach (Modifier m in EMMItem.GetActivePool(_oldHeldItem))
					{
						AddDetachItem(_oldHeldItem, m);
					}
				}

				// attach new held item
				if (player.HeldItem != null && !player.HeldItem.IsAir && IsMouseUsable(player.HeldItem))
				{
					foreach (Modifier m in EMMItem.GetActivePool(player.HeldItem))
					{
						AddAttachItem(player.HeldItem, m);
					}
				}

				_oldHeldItem = player.HeldItem;
			}
		}

		private bool UpdateMouseItemCache()
		{
			bool flag = (_oldMouseItem?.IsAir ?? true);

			if (_oldMouseItem == null || _oldMouseItem.IsNotTheSameAs(Main.mouseItem))
			{
				Ready = false;

				if (_oldMouseItem != null && !_oldMouseItem.IsAir && IsMouseUsable(_oldMouseItem))
				{
					foreach (Modifier m in EMMItem.GetActivePool(_oldMouseItem))
					{
						AddDetachItem(_oldMouseItem, m);
					}
				}

				if (Main.mouseItem != null && !Main.mouseItem.IsAir && IsMouseUsable(Main.mouseItem))
				{
					flag = false;

					// Detach first so we override 'IsActivated' again in case it's the same item
					if (_oldHeldItem != null && !_oldHeldItem.IsAir)
					{
						foreach (Modifier m in EMMItem.GetActivePool(_oldHeldItem))
						{
							AddDetachItem(_oldHeldItem, m);
						}
					}

					foreach (Modifier m in EMMItem.GetActivePool(Main.mouseItem))
					{
						AddAttachItem(Main.mouseItem, m);
					}
				}

				_oldMouseItem = Main.mouseItem;
			}

			return flag;
		}

		private void UpdateEquipsCache()
		{
			for (int i = 0; i < 8 + player.extraAccessorySlots; i++)
			{
				var oldEquip = _oldEquips[i];
				var newEquip = player.armor[i];

				// If equip slot needs an update
				if (_forceEquipUpdate || oldEquip == null || newEquip.IsNotTheSameAs(oldEquip))
				{
					Ready = false;

					// detach old first
					if (oldEquip != null && !oldEquip.IsAir)
					{
						foreach (Modifier m in EMMItem.GetActivePool(oldEquip))
						{
							AddDetachItem(oldEquip, m);
						}
					}

					// attach new
					if (newEquip != null && !newEquip.IsAir)
					{
						foreach (Modifier m in EMMItem.GetActivePool(newEquip))
						{
							AddAttachItem(newEquip, m);
						}
					}

					_oldEquips[i] = newEquip;
				}
			}
		}

		// This needs to be separate because of CheatSheetInterface static reference
		// to not freak out JIT
		private void UpdateCheatSheetCache()
		{
			// get cheat sheet slots
			var curEquips = CheatSheetInterface.GetEnabledExtraAccessories(player).Take(_oldCheatSheetEquips.Length).ToArray();

			// go over enabled slots
			for (int i = 0; i < curEquips.Length; i++)
			{
				var oldEquip = _oldCheatSheetEquips[i];
				var newEquip = curEquips[i];

				// update delegations
				if (oldEquip == null || newEquip.IsNotTheSameAs(oldEquip))
				{
					Ready = false;

					// detach old first
					if (oldEquip != null && !oldEquip.IsAir)
					{
						foreach (Modifier m in EMMItem.GetActivePool(oldEquip))
						{
							AddDetachItem(oldEquip, m);
						}
					}

					// attach new
					if (newEquip != null && !newEquip.IsAir)
					{
						foreach (Modifier m in EMMItem.GetActivePool(newEquip))
						{
							AddAttachItem(newEquip, m);
						}
					}

					_oldCheatSheetEquips[i] = newEquip;
				}
			}

			// current enabled is smaller than total
			if (curEquips.Length < _oldCheatSheetEquips.Length)
			{
				var outOfDateEquips = _oldCheatSheetEquips.Skip(curEquips.Length);
				if (outOfDateEquips.Any())
				{
					Ready = false;
				}

				// for all disabled slots but still had a registered item, detach it
				foreach (var item in outOfDateEquips.Where(x => x != null && !x.IsAir))
				{
					foreach (Modifier m in EMMItem.GetActivePool(item))
					{
						AddDetachItem(item, m);
					}
				}
			}
		}

		private void CacheModifierEffects(Player player)
		{
			bool anyDifferentEquip = player.armor.Take(8 + player.extraAccessorySlots)
				.Select((x, i) => new { Value = x, Index = i })
				.Any(x => _oldEquips[x.Index] != null && x.Value.IsNotTheSameAs(_oldEquips[x.Index]));

			// vanity, processed if hijacked to activate (antisocial)
			anyDifferentEquip &= player.armor.Skip(13)
				.Select((x, i) => new { Value = x, Index = i })
				.Any(x => _oldVanityEquips[x.Index] != null && x.Value.IsNotTheSameAs(_oldVanityEquips[x.Index]));

			// Only recache if needed, so check if there are changes
			if (_forceEquipUpdate || anyDifferentEquip)
			{
				_modifierEffects.Clear();

				for (int i = 0; i < 8 + player.extraAccessorySlots; i++)
				{
					var equip = player.armor[i];
					if (equip != null && !equip.IsAir)
					{
						CacheItemModifierEffects(equip);
					}
				}

				// vanity
				for (int k = 13; k < 18 + player.extraAccessorySlots; k++)
				{
					var equip = player.armor[k];
					if (equip != null
						&& !equip.IsAir
						&& ActivatedModifierItem.Item(equip).IsVanityActivated)
					{
						CacheItemModifierEffects(equip);
					}
				}
			}

			// else because in mouse should override held item
			if (Main.mouseItem != null && !Main.mouseItem.IsAir && Main.mouseItem.IsWeapon())
			{
				CacheItemModifierEffects(Main.mouseItem);
			}
			else if ((_oldMouseItem?.IsAir ?? true) && player.HeldItem != null && !player.HeldItem.IsAir && player.HeldItem.IsWeapon())
			{
				CacheItemModifierEffects(player.HeldItem);
			}
		}

		private void CacheItemModifierEffects(Item item)
		{
			var mods = EMMItem.GetActivePool(item);
			foreach (var modifier in mods)
			{
				var effectsAttribute = modifier
					.GetType()
					.GetCustomAttribute<UsesEffectAttribute>();

				if (effectsAttribute != null)
				{
					ModifierPlayer modPlayer = ModifierPlayer.Player(player);
					foreach (Type effect in effectsAttribute.Effects)
					{
						var modEffect = modPlayer.GetEffect(effect);
						if (modEffect != null && !_modifierEffects.Contains(modEffect.GetType()))
						{
							_modifierEffects.Add(modEffect.GetType());
						}
					}
				}
			}
		}
	}
}
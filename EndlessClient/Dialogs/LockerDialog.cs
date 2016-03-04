﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System.Collections.Generic;
using EOLib;
using EOLib.Graphics;
using EOLib.IO;
using EOLib.Net.API;
using Microsoft.Xna.Framework;
using XNAControls;

namespace EndlessClient.Dialogs
{
	public class LockerDialog : ScrollingListDialog
	{
		public static LockerDialog Instance { get; private set; }

		public static void Show(PacketAPI api, byte x, byte y)
		{
			if (Instance != null) return;

			Instance = new LockerDialog(api, x, y);

			if (!api.OpenLocker(x, y))
				EOGame.Instance.DoShowLostConnectionDialogAndReturnToMainMenu();
		}

		private static readonly string TITLE_FMT = OldWorld.Instance.MainPlayer.ActiveCharacter.Name + "'s " + OldWorld.GetString(DATCONST2.DIALOG_TITLE_PRIVATE_LOCKER) + " [{0}]";

		//map location of the currently open locker
		public byte X { get; private set; }
		public byte Y { get; private set; }

		private List<InventoryItem> items = new List<InventoryItem>();

		private LockerDialog(PacketAPI api, byte x, byte y)
			: base(api)
		{
			Title = string.Format(TITLE_FMT, 0);
			Buttons = ScrollingListDialogButtons.Cancel;
			ListItemType = ListDialogItem.ListItemStyle.Large;
			X = x;
			Y = y;

			DialogClosing += (o, e) => { Instance = null; X = 0; Y = 0; };
		}

		public void SetLockerData(List<InventoryItem> lockerItems)
		{
			ClearItemList();
			items = lockerItems;
			Title = string.Format(TITLE_FMT, lockerItems.Count);

			List<ListDialogItem> listItems = new List<ListDialogItem>();
			foreach (InventoryItem item in lockerItems)
			{
				ItemRecord rec = OldWorld.Instance.EIF.GetRecordByID(item.id);
				int amount = item.amount;
				ListDialogItem newItem = new ListDialogItem(this, ListDialogItem.ListItemStyle.Large)
				{
					Text = rec.Name,
					SubText = string.Format("x{0}  {1}", item.amount,
						rec.Type == ItemType.Armor
							? "(" + (rec.Gender == 0 ? OldWorld.GetString(DATCONST2.FEMALE) : OldWorld.GetString(DATCONST2.MALE)) + ")"
							: ""),
					IconGraphic = ((EOGame)Game).GFXManager.TextureFromResource(GFXTypes.Items, 2 * rec.Graphic - 1, true),
					OffsetY = 45
				};
				newItem.OnRightClick += (o, e) => _removeItem(rec, amount);

				listItems.Add(newItem);
			}

			SetItemList(listItems);
		}

		public int GetNewItemAmount(short id, int amount)
		{
			int matchIndex = items.FindIndex(_ii => _ii.id == id);
			if (matchIndex < 0) return amount;
			return items[matchIndex].amount + amount;
		}

		private void _removeItem(ItemRecord item, int amount)
		{
			if (!EOGame.Instance.Hud.InventoryFits((short)item.ID))
			{
				EOMessageBox.Show(OldWorld.GetString(DATCONST2.STATUS_LABEL_ITEM_PICKUP_NO_SPACE_LEFT),
					OldWorld.GetString(DATCONST2.STATUS_LABEL_TYPE_WARNING),
					XNADialogButtons.Ok, EOMessageBoxStyle.SmallDialogSmallHeader);
				return;
			}

			if (OldWorld.Instance.MainPlayer.ActiveCharacter.Weight + item.Weight * amount > OldWorld.Instance.MainPlayer.ActiveCharacter.MaxWeight)
			{
				EOMessageBox.Show(OldWorld.GetString(DATCONST2.DIALOG_ITS_TOO_HEAVY_WEIGHT),
					OldWorld.GetString(DATCONST2.STATUS_LABEL_TYPE_WARNING),
					XNADialogButtons.Ok, EOMessageBoxStyle.SmallDialogSmallHeader);
				return;
			}

			if (!m_api.LockerTakeItem(X, Y, (short)item.ID))
				EOGame.Instance.DoShowLostConnectionDialogAndReturnToMainMenu();
		}

		public override void Update(GameTime gt)
		{
			if (!Game.IsActive) return;
			if (EOGame.Instance.Hud.IsInventoryDragging())
			{
				shouldClickDrag = false;
				SuppressParentClickDrag(true);
			}
			else
			{
				shouldClickDrag = true;
				SuppressParentClickDrag(false);
			}

			base.Update(gt);
		}
	}
}
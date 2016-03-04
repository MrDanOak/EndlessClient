﻿// Original Work Copyright (c) Ethan Moffat 2014-2016
// This file is subject to the GPL v2 License
// For additional details, see the LICENSE file

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EndlessClient.Audio;
using EndlessClient.Dialogs;
using EndlessClient.HUD;
using EOLib;
using EOLib.Data.BLL;
using EOLib.Data.Map;
using EOLib.Graphics;
using EOLib.IO;
using EOLib.IO.Map;
using EOLib.Net.API;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace EndlessClient.Rendering
{
	public class MapRenderer : DrawableGameComponent
	{
		//collections
		private readonly Dictionary<Point, List<MapItem>> _mapItems = new Dictionary<Point, List<MapItem>>();
		private readonly List<CharacterRenderer> _characterRenderers = new List<CharacterRenderer>();
		private readonly List<NPCRenderer> _npcRenderers = new List<NPCRenderer>();
		private readonly object _npcListLock = new object(), _characterListLock = new object();

		public MapFile MapRef { get; private set; }
		private bool _needDispMapName;
		
		//public cursor members
		public bool MouseOver
		{
			get
			{
				MouseState ms = Mouse.GetState();
				return EOGame.Instance.IsActive && ms.X > 0 && ms.Y > 0 && ms.X < 640 && ms.Y < 320;
			}
		}
		public Point GridCoords { get { return _mouseCursorRenderer.GridCoords; } }

		//rendering members
		private RenderTarget2D _rtMapObjAbovePlayer, _rtMapObjBelowPlayer;
		private BlendState _playerBlend;
		private SpriteBatch _sb;

		private DateTime? _mapLoadTime;
		private int _transitionMetric;

		//animated tile/wall members
		private Vector2 _tileSrc;
		private int _wallSrcIndex;
		private TimeSpan? _lastAnimUpdate;
		private readonly List<Point> _visibleSpikeTraps = new List<Point>();
		private readonly object _spikeTrapsLock = new object();

		//door members
		private readonly Timer _doorTimer;
		private Warp _door;
		private byte _doorY; //since y-coord not stored in Warp object...

		private ManualResetEventSlim _drawingEvent;

		private readonly PacketAPI _api;

		private MiniMapRenderer _miniMapRenderer;
		private MouseCursorRenderer _mouseCursorRenderer;
		
		private bool _disposed;
		private readonly object _disposingLockObject = new object();

		public MapRenderer(Game g, PacketAPI apiHandle)
			: base(g)
		{
			if(g == null)
				throw new NullReferenceException("The game must not be null");
			if(!(g is EOGame))
				throw new ArgumentException("The game must be an EOGame instance");
			if(apiHandle == null || !apiHandle.Initialized)
				throw new ArgumentException("Invalid PacketAPI object");
			_api = apiHandle;

			_sb = new SpriteBatch(Game.GraphicsDevice);

			_drawingEvent = new ManualResetEventSlim(true);
			Visible = false;

			_doorTimer = new Timer(_doorTimerCallback);
		}

		#region /* PUBLIC INTERFACE -- CHAT + MAP RELATED */
		public void RenderChatMessage(TalkType messageType, int playerID, string message, ChatType chatType = ChatType.None)
		{
			//convert the messageType into a valid ChatTab to pass everything on to
			ChatTabs tab;
			switch (messageType)
			{
				case TalkType.NPC:
				case TalkType.Local: tab = ChatTabs.Local; break;
				case TalkType.Party: tab = ChatTabs.Group; break;
				default: throw new ArgumentOutOfRangeException("messageType", "Unsupported message type for chat rendering");
			}

			DrawableGameComponent dgc;
			string playerName = null;
			if (messageType == TalkType.NPC)
			{
				lock(_npcListLock)
					dgc = _npcRenderers.Find(_npc => _npc.NPC.Index == playerID);
				if (dgc != null)
					playerName = ((NPCRenderer)dgc).NPC.Data.Name;
			}
			else
			{
				lock (_characterListLock)
				{
					dgc = _characterRenderers.Find(_rend => _rend.Character.ID == playerID);
					if (dgc != null)
						playerName = ((CharacterRenderer) dgc).Character.Name;
				}
			}

			if (playerName == null) return;

			if(playerName.Length > 1)
				playerName = char.ToUpper(playerName[0]) + playerName.Substring(1);

			if (EOGame.Instance.Hud == null)
				return;

			message = EOChatRenderer.Filter(message, false);

			if (message != null)
			{
				EOGame.Instance.Hud.AddChat(tab, playerName, message, chatType);
				if (messageType == TalkType.Party)
				{
					//party chat also adds to local with the PM color
					EOGame.Instance.Hud.AddChat(ChatTabs.Local, playerName, message, chatType, ChatColor.PM);
				}
				MakeSpeechBubble(dgc, message, messageType == TalkType.Party);
			}
		}

		public void MakeSpeechBubble(DrawableGameComponent follow, string message, bool groupChat)
		{
			if (!OldWorld.Instance.ShowChatBubbles)
				return;

			if (follow == null)
				follow = OldWorld.Instance.ActiveCharacterRenderer; /* Calling with null assumes Active Character */

			//show just the speech bubble, since this should be called from the HUD and rendered there already

// ReSharper disable CanBeReplacedWithTryCastAndCheckForNull
			if (follow is CharacterRenderer)
				((CharacterRenderer)follow).SetChatBubbleText(message, groupChat);
			else if (follow is NPCRenderer)
				((NPCRenderer)follow).SetChatBubbleText(message, groupChat);
// ReSharper restore CanBeReplacedWithTryCastAndCheckForNull
		}

		public void SetActiveMap(MapFile newActiveMap)
		{
			_drawingEvent.Wait();
			_drawingEvent.Reset();

			if (MapRef != null && MapRef.Properties.AmbientNoise != 0)
				EOGame.Instance.SoundManager.StopLoopingSoundEffect(MapRef.Properties.AmbientNoise);
			
			MapRef = newActiveMap;

			if (_miniMapRenderer == null)
				_miniMapRenderer = new MiniMapRenderer(this);
			else
				_miniMapRenderer.Map = MapRef;

			if (!Game.Components.OfType<PlayerStatusIconRenderer>().Any())
			{
				var iconRenderer = new PlayerStatusIconRenderer((EOGame)Game);
				Game.Components.Add(iconRenderer);
			}

			_mapItems.Clear();
			lock (_characterListLock)
			{
				_characterRenderers.ForEach(_rend => _rend.Dispose());
				_characterRenderers.Clear();
			}

			lock (_npcListLock)
			{
				_npcRenderers.ForEach(_npc => _npc.Dispose());
				_npcRenderers.Clear();
			}
			lock (_spikeTrapsLock)
				_visibleSpikeTraps.Clear();

			//need to reset door-related members when changing maps.
			if (_door != null)
			{
				_door.doorOpened = false;
				_door.backOff = false;
				_door = null;
				_doorY = 0;
				_doorTimer.Change(Timeout.Infinite, Timeout.Infinite);
			}

			_mapLoadTime = DateTime.Now;
			_transitionMetric = 1;
			if (!MapRef.Properties.MapAvailable)
				_miniMapRenderer.Visible = false;

			if (MapRef.Properties.Name.Length > 0)
			{
				if (EOGame.Instance.Hud != null)
					EOGame.Instance.Hud.AddChat(ChatTabs.System, "", OldWorld.GetString(DATCONST2.STATUS_LABEL_YOU_ENTERED) + " " + MapRef.Properties.Name, ChatType.NoteLeftArrow);
				else
					_needDispMapName = true;
			}

			PlayOrStopBackgroundMusic();
			PlayOrStopAmbientNoise();

			_drawingEvent.Set();
		}

		public ITileInfo GetTileInfo(byte destX, byte destY)
		{
			if (MapRef.Properties.Width < destX || MapRef.Properties.Height < destY)
				return new BasicTileInfoWithSpec(TileSpec.MapEdge);

			lock (_npcListLock)
			{
				int ndx;
				if ((ndx = _npcRenderers.FindIndex(_npc => (!_npc.NPC.Walking && _npc.NPC.X == destX && _npc.NPC.Y == destY)
					|| _npc.NPC.Walking && _npc.NPC.DestX == destX && _npc.NPC.DestY == destY)) >= 0)
				{
					NPCRenderer retNPC = _npcRenderers[ndx];
					if (!retNPC.NPC.Dying)
						return new NPCTileInfo(retNPC.NPC);
				}
			}

			lock (_characterListLock)
			{
				if (_characterRenderers.Select(x => x.Character).Any(player => player.X == destX && player.Y == destY))
					return new BasicTileInfo(TileInfoReturnType.IsOtherPlayer);
			}

			var warp = MapRef.Warps[destY, destX];
			if (warp != null)
				return new WarpTileInfo(warp);

			var sign = MapRef.Signs.Find(_ms => _ms.X == destX && _ms.Y == destY);
			if (sign.X == destX && sign.Y == destY)
				return new MapSignTileInfo(sign);

			if(destX <= MapRef.Properties.Width && destY <= MapRef.Properties.Height)
			{
				var tile = MapRef.Tiles[destY, destX];
				if (tile != TileSpec.None)
					return new BasicTileInfoWithSpec(tile);
			}
			
			//don't need to check zero bounds: because byte type is always positive (unsigned)
			return destX <= MapRef.Properties.Width && destY <= MapRef.Properties.Height
				? new BasicTileInfoWithSpec(TileSpec.None)
				: new BasicTileInfoWithSpec(TileSpec.MapEdge);
		}

		public void ToggleMapView()
		{
			if (MapRef.Properties.MapAvailable)
				_miniMapRenderer.Visible = !_miniMapRenderer.Visible;
			else
				EOGame.Instance.Hud.SetStatusLabel(DATCONST2.STATUS_LABEL_TYPE_WARNING, DATCONST2.STATUS_LABEL_NO_MAP_OF_AREA);
		}

		public void AddMapItem(MapItem newItem)
		{
			if (newItem.IsNPCDrop && newItem.ItemID > 0)
			{
				ItemRecord rec = OldWorld.Instance.EIF.GetRecordByID(newItem.ItemID);
				EOGame.Instance.Hud.AddChat(ChatTabs.System, "",
					string.Format("{0} {1} {2}", OldWorld.GetString(DATCONST2.STATUS_LABEL_THE_NPC_DROPPED), newItem.Amount, rec.Name),
					ChatType.DownArrow);
			}

			Point key = new Point(newItem.X, newItem.Y);
			if(!_mapItems.ContainsKey(key))
				_mapItems.Add(key, new List<MapItem>());

			int index = _mapItems.Values
								 .SelectMany(x => x.ToList())
								 .ToList()
								 .FindIndex(_mi => _mi.UniqueID == newItem.UniqueID);
			if (index < 0)
				_mapItems[key].Add(newItem);
		}

		public void RemoveMapItem(short uid)
		{
			var locationContainingItemUID = _mapItems.Keys.FirstOrDefault(_key => _mapItems[_key].Find(_mi => _mi.UniqueID == uid).UniqueID == uid);
			
			List<MapItem> res = _mapItems[locationContainingItemUID];
			for (int i = res.Count - 1; i >= 0; --i)
			{
				if (res[i].UniqueID == uid)
				{
					RemoveMapItem(res[i]);
					break;
				}
			}
		}

		private void RemoveMapItem(MapItem oldItem)
		{
			Point key = new Point(oldItem.X, oldItem.Y);
			if (!_mapItems.ContainsKey(key))
				return;
			_mapItems[key].Remove(oldItem);
			if (_mapItems[key].Count == 0)
				_mapItems.Remove(key);
		}

		public void UpdateMapItemAmount(short uid, int amountTaken)
		{
			var pt = _mapItems.Keys.Single(_key => _mapItems[_key].Find(_mi => _mi.UniqueID == uid).UniqueID == uid);

			List<MapItem> res = _mapItems[pt];
			var toRemove = res.Single(_mi => _mi.UniqueID == uid);
			res.Remove(toRemove);
			toRemove = new MapItem
			{
				Amount = toRemove.Amount - amountTaken,
				ItemID = toRemove.ItemID,
				IsNPCDrop = toRemove.IsNPCDrop,
				OwningPlayerID = toRemove.OwningPlayerID,
				DropTime = toRemove.DropTime,
				UniqueID = toRemove.UniqueID,
				X = toRemove.X,
				Y = toRemove.Y
			};
			//still some left. add it back.
			if(toRemove.Amount > 0)
				res.Add(toRemove);
		}

		public MapItem GetMapItemAt(int x, int y)
		{
			var p = new Point(x, y);
			if (_mapItems.ContainsKey(p) && _mapItems[p].Count > 0)
				return _mapItems[p].Last();

			return null;
		}

		public void ClearMapItems()
		{
			_mapItems.Clear();
		}

		public void PlayOrStopBackgroundMusic()
		{
			if (!OldWorld.Instance.MusicEnabled)
			{
				EOGame.Instance.SoundManager.StopBackgroundMusic();
				return;
			}

			//not sure what MusicExtra field is supposed to be for
			if (MapRef.Properties.Music > 0)
			{
				//sound manager accounts for zero-based indices when playing music
				EOGame.Instance.SoundManager.PlayBackgroundMusic(MapRef.Properties.Music);
			}
			else
			{
				EOGame.Instance.SoundManager.StopBackgroundMusic();
			}
		}

		public void PlayOrStopAmbientNoise()
		{
			if (!OldWorld.Instance.SoundEnabled)
			{
				if (MapRef.Properties.AmbientNoise > 0)
					EOGame.Instance.SoundManager.StopLoopingSoundEffect(MapRef.Properties.AmbientNoise);
				return;
			}

			if (MapRef.Properties.AmbientNoise > 0)
				EOGame.Instance.SoundManager.PlayLoopingSoundEffect(MapRef.Properties.AmbientNoise);
		}

		#endregion

		#region /* PUBLIC INTERFACE -- OTHER PLAYERS */
		public void AddOtherPlayer(CharacterData c, WarpAnimation anim = WarpAnimation.None)
		{
			CharacterRenderer otherRend = null;
			lock (_characterListLock)
			{
				Character other = _characterRenderers.Select(x => x.Character).FirstOrDefault(x => x.Name == c.Name && x.ID == c.ID);
				if (other == null)
				{
					other = new Character(_api, c);
					lock (_characterListLock)
					{
						_characterRenderers.Add(otherRend = new CharacterRenderer(other));
						_characterRenderers[_characterRenderers.Count - 1].Visible = true;
						_characterRenderers[_characterRenderers.Count - 1].Initialize();
					}
					other.RenderData.SetUpdate(true);
				}
				else
				{
					other.ApplyData(c);
				}
			}

			if (anim == WarpAnimation.Admin && otherRend != null)
				otherRend.ShowWarpArrive();
		}

		public void RemoveOtherPlayer(short id, WarpAnimation anim = WarpAnimation.None)
		{
			lock (_characterListLock)
			{
				int ndx;
				if ((ndx = _characterRenderers.FindIndex(cc => cc.Character.ID == id)) >= 0)
				{
					var rend = _characterRenderers[ndx];
					rend.HideChatBubble();
					rend.Visible = false;
					_characterRenderers.Remove(rend);

					if (anim == WarpAnimation.Admin)
					{
						rend.ShowWarpLeave();
					}
					else
					{
						rend.Close();
					}
				}
			}
		}

		public void ClearOtherPlayers()
		{
			lock (_characterListLock)
			{
				_characterRenderers.ForEach(_rend => _rend.HideChatBubble());
				_characterRenderers.Clear();
			}
		}

		public void OtherPlayerFace(short ID, EODirection direction)
		{
			lock (_characterListLock)
			{
				int ndx;
				if ((ndx = _characterRenderers.FindIndex(x => x.Character.ID == ID)) >= 0)
					_characterRenderers[ndx].Character.RenderData.SetDirection(direction);
			}
		}

		public void OtherPlayerWalk(short ID, EODirection direction, byte x, byte y)
		{
			lock (_characterListLock)
			{
				CharacterRenderer rend = _characterRenderers.Find(_rend => _rend.Character.ID == ID);
				if (rend != null)
				{
					rend.Character.Walk(direction, x, y, false);

					var ti = GetTileInfo(rend.Character.DestX, rend.Character.DestY);
					bool isWater = ti.ReturnType == TileInfoReturnType.IsTileSpec && ti.Spec == TileSpec.Water;
					bool isSpike = ti.ReturnType == TileInfoReturnType.IsTileSpec && ti.Spec == TileSpec.SpikesTrap;
					rend.PlayerWalk(isWater, isSpike);
				}
			}
		}

		public void OtherPlayerAttack(short ID, EODirection direction)
		{
			lock (_characterListLock)
			{
				CharacterRenderer rend = _characterRenderers.Find(_rend => _rend.Character.ID == ID);
				if (rend != null)
				{
					rend.Character.Attack(direction);

					var info = GetTileInfo((byte) rend.Character.X, (byte) rend.Character.Y);
					rend.PlayerAttack(info.ReturnType == TileInfoReturnType.IsTileSpec && info.Spec == TileSpec.Water);
				}
			}
		}

		public void OtherPlayerEmote(short playerID, Emote emote)
		{
			lock (_characterListLock)
			{
				CharacterRenderer rend = _characterRenderers.Find(cc => cc.Character.ID == playerID);
				if (rend != null)
				{
					rend.Character.Emote(emote);
					rend.PlayerEmote();
				}
			}
		}

		public void OtherPlayerHide(short ID, bool hidden)
		{
			lock (_characterListLock)
			{
				int ndx;
				if ((ndx = _characterRenderers.FindIndex(x => x.Character.ID == ID)) >= 0)
				{
					_characterRenderers[ndx].Character.RenderData.SetHidden(hidden);
				}
			}
		}

		public void OtherPlayerHeal(short ID, int healAmount, int pctHealth)
		{
			lock (_characterListLock)
			{
				CharacterRenderer rend = ID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID
					? OldWorld.Instance.ActiveCharacterRenderer
					: _characterRenderers.Find(_rend => _rend.Character.ID == ID);

				if (rend == null) return; //couldn't find other player :(

				if (healAmount > 0)
				{
					rend.Character.Stats.HP = (short) Math.Max(rend.Character.Stats.HP + healAmount, rend.Character.Stats.MaxHP);
					if (rend.Character == OldWorld.Instance.MainPlayer.ActiveCharacter)
					{
						//update health in UI
						EOGame.Instance.Hud.RefreshStats();
					}
					rend.SetDamageCounterValue(healAmount, pctHealth, true);
				}
			}
		}

		public void OtherPlayerShoutSpell(short playerID, short spellID)
		{
			string shoutName = OldWorld.Instance.ESF.GetRecordByID(spellID).Shout;

			lock (_characterListLock)
			{
				var renderer = _characterRenderers.Find(x => x.Character.ID == playerID);
				if (renderer != null)
					renderer.SetSpellShout(shoutName);
			}
		}

		public void PlayerCastSpellSelf(short fromPlayerID, short spellID, int spellHP, byte percentHealth)
		{
			lock (_characterListLock)
			{
				var renderer = _characterRenderers.Find(x => x.Character.ID == fromPlayerID);
				if (renderer != null)
				{
					renderer.StopShouting(false);
					renderer.StartCastingSpell();
					renderer.SetDamageCounterValue(spellHP, percentHealth, true);
				}
				else if (fromPlayerID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID)
				{
					renderer = OldWorld.Instance.ActiveCharacterRenderer;
					renderer.SetDamageCounterValue(spellHP, percentHealth, true);
				}

				_renderSpellOnPlayer(spellID, renderer);
			}
		}

		public void PlayerCastSpellTarget(short fromPlayerID, short targetPlayerID, EODirection fromPlayerDirection, short spellID, int recoveredHP, byte targetPercentHealth)
		{
			lock (_characterListLock)
			{
				bool fromIsMain = false;
				var fromRenderer = _characterRenderers.Find(x => x.Character.ID == fromPlayerID);
				var toRenderer = _characterRenderers.Find(x => x.Character.ID == targetPlayerID);

				if (fromRenderer == null && fromPlayerID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID)
				{
					fromIsMain = true;
					fromRenderer = OldWorld.Instance.ActiveCharacterRenderer;
				}

				if (toRenderer == null && targetPlayerID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID)
					toRenderer = OldWorld.Instance.ActiveCharacterRenderer;

				if (fromRenderer != null) //do source renderer stuff
				{
					if (!fromIsMain)
					{
						bool showShoutName = fromRenderer != toRenderer;
						fromRenderer.StopShouting(showShoutName);
						fromRenderer.StartCastingSpell();
					}
					fromRenderer.Character.RenderData.SetDirection(fromPlayerDirection);
				}

				if (toRenderer != null) //do target renderer stuff
				{
					toRenderer.SetDamageCounterValue(recoveredHP, targetPercentHealth, true);
					_renderSpellOnPlayer(spellID, toRenderer);
				}
			}
		}

		public void PlayerCastSpellGroup(short fromPlayerID, short spellID, short spellHPgain, List<GroupSpellTarget> spellTargets)
		{
			lock (_characterListLock)
			{
				bool fromIsMain = false;
				var fromRenderer = _characterRenderers.Find(x => x.Character.ID == fromPlayerID);
				if (fromRenderer == null && fromPlayerID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID)
				{
					fromIsMain = true;
					fromRenderer = OldWorld.Instance.ActiveCharacterRenderer;
				}

				if (fromRenderer != null && !fromIsMain)
				{
					fromRenderer.StopShouting(false);
					fromRenderer.StartCastingSpell();
				}

				foreach (var target in spellTargets)
				{
					bool targetIsMain = false;
					var targetRenderer = _characterRenderers.Find(x => x.Character.ID == target.MemberID);
					if (targetRenderer == null && target.MemberID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID)
					{
						targetIsMain = true;
						targetRenderer = OldWorld.Instance.ActiveCharacterRenderer;
					}

					if (targetRenderer == null) continue;

					if (targetIsMain)
						targetRenderer.Character.Stats.HP = target.MemberHP;
					targetRenderer.SetDamageCounterValue(spellHPgain, target.MemberPercentHealth, true);

					_renderSpellOnPlayer(spellID, targetRenderer);
				}
			}
		}

		public void UpdateOtherPlayers()
		{
			//when mainplayer walks, tell other players to update!
			lock (_characterListLock)
				_characterRenderers.Select(x => x.Character).ToList().ForEach(x => x.RenderData.SetUpdate(true));
		}

		public void UpdateOtherPlayerRenderData(short playerId, bool sound, CharRenderData newRenderData)
		{
			Character c = playerId == OldWorld.Instance.MainPlayer.ActiveCharacter.ID
				? OldWorld.Instance.MainPlayer.ActiveCharacter
				: GetOtherPlayerByID(playerId);

			if (c != null)
			{
				c.SetDisplayItemsFromRenderData(newRenderData);
				//todo: play sound?
			}
		}

		public void UpdateOtherPlayerHairData(short playerId, byte hairColor, byte hairStyle = 255)
		{
			Character c = playerId == OldWorld.Instance.MainPlayer.ActiveCharacter.ID
				? OldWorld.Instance.MainPlayer.ActiveCharacter
				: GetOtherPlayerByID(playerId);

			if (c != null)
			{
				c.RenderData.SetHairColor(hairColor);
				if (hairStyle != 255) c.RenderData.SetHairStyle(hairStyle);
			}
		}

		public Character GetOtherPlayerByID(short playerId)
		{
			Character retChar;
			lock (_characterListLock)
				retChar = _characterRenderers.Find(_c => _c.Character.ID == playerId).Character;
			return retChar;
		}

		public void ShowContextMenu(CharacterRenderer player)
		{
			_mouseCursorRenderer.ShowContextMenu(player);
		}

		public void ShowPotionEffect(short playerID, int effectID)
		{
			CharacterRenderer renderer;
			lock (_characterListLock)
				renderer = _characterRenderers.SingleOrDefault(x => x.Character.ID == playerID);
			if (renderer != null)
				renderer.ShowPotionAnimation(effectID);
		}

		#endregion

		#region/* PUBLIC INTERFACE -- OTHER NPCS */

		public NPCRenderer GetNPCAt(int x, int y)
		{
			lock (_npcListLock)
			{
				return _npcRenderers.Find(_npc => _npc.NPC.X == x && _npc.NPC.Y == y);
			}
		}

		public void AddOtherNPC(NPCData data)
		{
			lock (_npcListLock)
			{
				var fileData = OldWorld.Instance.ENF.GetRecordByID(data.ID);
				NPCRenderer newNpcRenderer = new NPCRenderer(new NPC(data, fileData));
				newNpcRenderer.Initialize();
				newNpcRenderer.Visible = true;
				_npcRenderers.Add(newNpcRenderer);
			}
		}

		public void RemoveOtherNPC(byte index, int damage = 0, short playerID = 0, EODirection playerDirection = (EODirection)0, short spellID = -1)
		{
			lock (_npcListLock)
			{
				NPCRenderer npcRenderer = _npcRenderers.Find(_npc => _npc.NPC.Index == index);
				if (npcRenderer != null)
				{
					if (damage > 0) //npc was killed - will do cleanup later
					{
						npcRenderer.TakeDamageFrom(null, damage, 0);
						npcRenderer.Kill();

						_renderSpellOnNPC(spellID, npcRenderer);
					}
					else //npc is out of view or done fading away
					{
						npcRenderer.Visible = false;
						npcRenderer.HideChatBubble();
						npcRenderer.Dispose();
						_npcRenderers.Remove(npcRenderer);
					}
				}
			}

			if (playerID > 0)
			{
				lock (_characterListLock)
				{
					var renderer = _characterRenderers.Find(x => x.Character.ID == playerID);
					if (renderer != null)
					{
						renderer.Character.RenderData.SetDirection(playerDirection);
						renderer.StopShouting(true);
						renderer.StartCastingSpell();
					}
					else if (playerID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID)
						OldWorld.Instance.ActiveCharacterRenderer.Character.RenderData.SetDirection(playerDirection);
				}
			}
		}

		public void RemoveNPCsWhere(Func<NPCRenderer, bool> predicate)
		{
			List<byte> indexes;
			lock (_npcListLock)
				indexes = _npcRenderers.Where(predicate).Select(x => x.NPC.Index).ToList();
			indexes.ForEach(x => RemoveOtherNPC(x));
		}

		public void ClearOtherNPCs()
		{
			lock (_npcListLock)
			{
				foreach (NPCRenderer n in _npcRenderers)
				{
					n.Visible = false;
					n.Dispose();
				}
				_npcRenderers.Clear();
			}
		}

		public void NPCWalk(byte index, byte x, byte y, EODirection dir)
		{
			lock (_npcListLock)
			{
				NPCRenderer toWalk = _npcRenderers.Find(_npc => _npc.NPC.Index == index);
				if (toWalk != null && !toWalk.NPC.Walking)
				{
					toWalk.Walk(x, y, dir);
				}
			}
		}

		public void NPCAttack(byte index, bool isTargetPlayerDead, EODirection dir, short targetPlayerId, int damageToPlayer, int playerPctHealth)
		{
			lock (_npcListLock)
			{
				NPCRenderer toAttack = _npcRenderers.Find(_npc => _npc.NPC.Index == index);
				if (toAttack != null && !toAttack.NPC.Attacking)
				{
					toAttack.Attack(dir);
				}
			}

			lock (_characterListLock)
			{
				CharacterRenderer rend = targetPlayerId == OldWorld.Instance.MainPlayer.ActiveCharacter.ID
					? OldWorld.Instance.ActiveCharacterRenderer
					: _characterRenderers.Find(_rend => _rend.Character.ID == targetPlayerId);

				if (rend == null) return; //couldn't find other player :(

				rend.Character.Stats.HP = (short) Math.Max(rend.Character.Stats.HP - damageToPlayer, 0);
				if (rend.Character == OldWorld.Instance.MainPlayer.ActiveCharacter && ((EOGame) Game).Hud != null)
				{
					//update health in UI
					((EOGame) Game).Hud.RefreshStats();
				}
				rend.SetDamageCounterValue(damageToPlayer, playerPctHealth);

				if (isTargetPlayerDead)
					rend.Die();
			}
		}

		public void NPCTakeDamage(short npcIndex, short fromPlayerID, EODirection fromDirection, int damageToNPC, int npcPctHealth, short spellID = -1)
		{
			lock (_npcListLock)
			{
				NPCRenderer toDamage = _npcRenderers.Find(_npc => _npc.NPC.Index == npcIndex);
				if (toDamage == null) return;

				_renderSpellOnNPC(spellID, toDamage);
				
				Character opponent = null;
				lock (_characterRenderers)
				{
					var rend = fromPlayerID == OldWorld.Instance.MainPlayer.ActiveCharacter.ID
						? OldWorld.Instance.ActiveCharacterRenderer
						: _characterRenderers.Find(_rend => _rend.Character.ID == fromPlayerID);

					if (rend != null)
					{
						if (rend.Character.RenderData.facing != fromDirection)
							rend.Character.RenderData.SetDirection(fromDirection);
						opponent = rend.Character;
					}
				}

				toDamage.TakeDamageFrom(opponent, damageToNPC, npcPctHealth);
			}
		}

		#endregion

		#region /* PUBLIC INTERFACE -- DOORS */

		public void StartOpenDoor(Warp warpRef, byte x, byte y)
		{
			warpRef.backOff = true; //set flag to prevent hella door packets from the client
			if(!_api.DoorOpen(x, y))
				((EOGame)Game).DoShowLostConnectionDialogAndReturnToMainMenu();
		}

		public void OnDoorOpened(byte x, byte y)
		{
			if (_door != null && _door.doorOpened)
			{
				_door.doorOpened = false;
				_door.backOff = false;
				_doorY = 0;
			}

			if ((_door = MapRef.Warps[y, x]) != null)
			{
				if(OldWorld.Instance.SoundEnabled)
					((EOGame) Game).SoundManager.GetSoundEffectRef(SoundEffectID.DoorOpen).Play();
				_door.doorOpened = true;
				_doorY = y;
				_doorTimer.Change(3000, 0);
			}
		}

		private void _doorTimerCallback(object state)
		{
			if (_door == null)
			{
				_doorY = 0;
				return;
			}

			if (_door.doorOpened && OldWorld.Instance.SoundEnabled)
				((EOGame) Game).SoundManager.GetSoundEffectRef(SoundEffectID.DoorClose).Play();

			_door.doorOpened = false;
			_door.backOff = false; //back-off from sending a door packet.
			_doorY = 0;
			_doorTimer.Change(Timeout.Infinite, Timeout.Infinite);
		}

#endregion

		#region /* PUBLIC INTERFACE -- MAP EFFECTS */

		public void PlayTimedSpikeSoundEffect()
		{
			if (!MapRef.Properties.HasTimedSpikes) return;

			if (OldWorld.Instance.SoundEnabled)
				((EOGame) Game).SoundManager.GetSoundEffectRef(SoundEffectID.Spikes).Play();
		}

		public void SpikeDamage(short damage, short hp, short maxhp)
		{
			var rend = OldWorld.Instance.ActiveCharacterRenderer;
			rend.Character.Stats.HP = hp;
			rend.Character.Stats.MaxHP = maxhp;
			((EOGame)Game).Hud.RefreshStats();

			int percentHealth = (int)Math.Round(((double)hp / maxhp) * 100.0);

			_spikeDamageShared(rend, damage, percentHealth, hp == 0);
		}

		public void SpikeDamage(short playerID, int percentHealth, bool isPlayerDead, int damageAmount)
		{
			lock (_characterListLock)
			{
				int ndx = _characterRenderers.FindIndex(_rend => _rend.Character.ID == playerID);
				if (ndx < 0) return;

				var rend = _characterRenderers[ndx];
				_spikeDamageShared(rend, damageAmount, percentHealth, isPlayerDead);
			}
		}

		private void _spikeDamageShared(CharacterRenderer rend, int damageAmount, int percentHealth, bool isPlayerDead)
		{
			rend.SetDamageCounterValue(damageAmount, percentHealth);
			if (isPlayerDead)
				rend.Die();
		}

		public void AddVisibleSpikeTrap(int x, int y)
		{
			lock (_spikeTrapsLock)
			{
				if (MapRef.Tiles[y, x] != TileSpec.SpikesTrap)
					throw new ArgumentException("The specified tile location is not a trap spike");

				if (_visibleSpikeTraps.Contains(new Point(x, y)))
					return;
				_visibleSpikeTraps.Add(new Point(x, y));
			}
		}

		public void RemoveVisibleSpikeTrap(int x, int y)
		{
			lock (_spikeTrapsLock)
			{
				int ndx = _visibleSpikeTraps.FindIndex(pt => pt.X == x && pt.Y == y);
				if (ndx < 0) return;
				_visibleSpikeTraps.RemoveAt(ndx);
			}
		}

		public void DrainHPFromPlayers(short damage, short hp, short maxhp, IEnumerable<TimedMapHPDrainData> otherCharacterData)
		{
			if (MapRef.Properties.Effect != MapEffect.HPDrain) return;

			int percentHealth = (int)Math.Round(((double)hp / maxhp) * 100.0);

			var mainRend = OldWorld.Instance.ActiveCharacterRenderer;
			mainRend.Character.Stats.HP = hp;
			mainRend.Character.Stats.MaxHP = maxhp;
			mainRend.SetDamageCounterValue(damage, percentHealth);
			
			((EOGame)Game).Hud.RefreshStats();

			lock (_characterListLock)
			{
				foreach (var other in otherCharacterData)
				{
					int ndx = _characterRenderers.FindIndex(_rend => _rend.Character.ID == other.PlayerID);
					if (ndx < 0) continue;

					var rend = _characterRenderers[ndx];
					rend.SetDamageCounterValue(other.DamageDealt, other.PlayerPercentHealth);
				}
			}

			if (OldWorld.Instance.SoundEnabled)
				((EOGame) Game).SoundManager.GetSoundEffectRef(SoundEffectID.MapEffectHPDrain).Play();
		}

		public void DrainTPFromMainPlayer(short amount, short tp, short maxtp)
		{
			if (MapRef.Properties.Effect != MapEffect.TPDrain || amount == 0) return;

			OldWorld.Instance.MainPlayer.ActiveCharacter.Stats.TP = tp;
			OldWorld.Instance.MainPlayer.ActiveCharacter.Stats.MaxTP = maxtp;
			((EOGame)Game).Hud.RefreshStats();

			if (OldWorld.Instance.SoundEnabled)
				((EOGame) Game).SoundManager.GetSoundEffectRef(SoundEffectID.MapEffectTPDrain).Play();
		}

		#endregion

		#region /* GAME COMPONENT DERIVED METHODS */

		public override void Initialize()
		{
			_rtMapObjAbovePlayer = new RenderTarget2D(Game.GraphicsDevice, 
				Game.GraphicsDevice.PresentationParameters.BackBufferWidth, 
				Game.GraphicsDevice.PresentationParameters.BackBufferHeight,
				false,
				SurfaceFormat.Color,
				DepthFormat.None);

			_rtMapObjBelowPlayer = new RenderTarget2D(Game.GraphicsDevice,
				Game.GraphicsDevice.PresentationParameters.BackBufferWidth, 
				Game.GraphicsDevice.PresentationParameters.BackBufferHeight,
				false,
				SurfaceFormat.Color,
				DepthFormat.None);

			_playerBlend = new BlendState
			{
				BlendFactor = new Color(255, 255, 255, 64),

				AlphaSourceBlend = Blend.One,
				AlphaDestinationBlend = Blend.One,
				AlphaBlendFunction = BlendFunction.Add,

				ColorSourceBlend = Blend.BlendFactor,
				ColorDestinationBlend = Blend.One
			};

			_mouseCursorRenderer = new MouseCursorRenderer((EOGame)Game, this);

			base.Initialize();
		}

		public override void Update(GameTime gameTime)
		{
			//***update for all objects on map
			_updateCharacters(gameTime);
			_updateNPCs(gameTime);

			//***do the map animations
			_animateWallTiles(gameTime);

			//***do the cursor stuff
			if (MouseOver)
				_mouseCursorRenderer.Update();

			if (_needDispMapName && EOGame.Instance.Hud != null)
			{
				_needDispMapName = false;
				EOGame.Instance.Hud.AddChat(ChatTabs.System, "", OldWorld.GetString(DATCONST2.STATUS_LABEL_YOU_ENTERED) + " " + MapRef.Properties.Name, ChatType.NoteLeftArrow);
			}

			if (_drawingEvent == null) return;

			//draw stuff to the render target
			//this is done in update instead of draw because I'm using render targets
			_drawingEvent.Wait(); //need to make sure that the map isn't being changed during a draw!
			_drawingEvent.Reset();
			_drawMapObjectsAndActors();
			_drawingEvent.Set(); //todo: there is a deadlock when switching maps

			base.Update(gameTime);
		}

		private void _updateCharacters(GameTime gameTime)
		{
			OldWorld.Instance.ActiveCharacterRenderer.Update(gameTime);
			lock (_characterListLock)
			{
				foreach (CharacterRenderer rend in _characterRenderers)
					rend.Update(gameTime); //do update logic here: other renderers will NOT be added to Game's components

				var deadRenderers = _characterRenderers.Where(x => x.CompleteDeath);
				foreach (var rend in deadRenderers)
				{
					RemoveOtherPlayer((short) rend.Character.ID);

					if (_visibleSpikeTraps.Contains(new Point(rend.Character.X, rend.Character.Y)) &&
						!_characterRenderers.Select(x => x.Character)
							.Any(player => player.X == rend.Character.X && player.Y == rend.Character.Y))
					{
						RemoveVisibleSpikeTrap(rend.Character.X, rend.Character.Y);
					}
				}
			}
		}

		private void _updateNPCs(GameTime gameTime)
		{
			lock (_npcListLock)
			{
				foreach (var npc in _npcRenderers)
					npc.Update(gameTime);

				var deadNPCs = _npcRenderers.Where(x => x.NPC.DeathCompleted).ToList();
				foreach (var npc in deadNPCs)
				{
					RemoveOtherNPC(npc.NPC.Index);
				}
			}
		}

		private void _animateWallTiles(GameTime gameTime)
		{
			//lazy init
			if (_lastAnimUpdate == null) _lastAnimUpdate = gameTime.TotalGameTime;
			if ((gameTime.TotalGameTime - _lastAnimUpdate.Value).TotalMilliseconds > 500)
			{
				_wallSrcIndex++;
				if (_wallSrcIndex == 4) _wallSrcIndex = 0;

				_tileSrc = new Vector2(64 + _tileSrc.X, 0);
				if (_tileSrc.X > 192)
					_tileSrc = Vector2.Zero;

				_lastAnimUpdate = gameTime.TotalGameTime;
			}
		}

		public override void Draw(GameTime gameTime)
		{
			if (MapRef != null)
			{
				if (_drawingEvent == null)
					_drawingEvent = new ManualResetEventSlim(true);

				_drawingEvent.Wait();
				_drawingEvent.Reset();

				_drawGroundLayer();
				if(_mapItems.Count > 0)
					_drawMapItems();

				if (_mapLoadTime != null && (DateTime.Now - _mapLoadTime.Value).TotalMilliseconds > 2000)
					_mapLoadTime = null;

				_sb.Begin();

				_mouseCursorRenderer.Draw(_sb);

				/*_drawPlayersNPCsAndMapObjects()*/
				_sb.Draw(_rtMapObjAbovePlayer, Vector2.Zero, Color.White);
				_sb.Draw(_rtMapObjBelowPlayer, Vector2.Zero, Color.White);
#if DEBUG
				_sb.DrawString(EOGame.Instance.DBGFont, string.Format("FPS: {0}", OldWorld.FPS), new Vector2(30, 30), Color.White);
#endif
				_sb.End();

				if (_miniMapRenderer.Visible)
					_miniMapRenderer.Draw();

				_drawingEvent.Set();
			}

			base.Draw(gameTime);
		}

		/* DRAWING-RELATED HELPER METHODS */
		// Special Thanks: HotDog's client. Used heavily as a reference for numeric offsets/techniques, with some adjustments here and there.
		private void _drawGroundLayer()
		{
			Character c = OldWorld.Instance.MainPlayer.ActiveCharacter;
			const int localViewLength = 10;
			int xMin = c.X - localViewLength < 0 ? 0 : c.X - localViewLength,
				xMax = c.X + localViewLength > MapRef.Properties.Width ? MapRef.Properties.Width : c.X + localViewLength;
			int yMin = c.Y - localViewLength < 0 ? 0 : c.Y - localViewLength,
				yMax = c.Y + localViewLength > MapRef.Properties.Height ? MapRef.Properties.Height : c.Y + localViewLength;
			int cOffX = c.OffsetX, cOffY = c.OffsetY;

			Texture2D fillTileRef = null;
			for (int i = yMin; i <= yMax; ++i)
			{
				_sb.Begin();

				for (int j = xMin; j <= xMax; ++j)
				{
					Vector2 pos = GetDrawCoordinatesFromGridUnits(j, i, cOffX, cOffY);

					//only render fill layer when the ground layer is not present!
					if (MapRef.Properties.FillTile > 0 && MapRef.GFX[0][i, j] < 0)
					{
						if (fillTileRef == null) //only do the cache lookup once!
							fillTileRef = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapTiles, MapRef.Properties.FillTile, true);

						_sb.Draw(fillTileRef, new Vector2(pos.X - 1, pos.Y - 2),
							Color.FromNonPremultiplied(255, 255, 255, _getAlpha(j, i, c)));
					}

					//ground layer next
					int tile;
					if ((tile = MapRef.GFX[0][i, j]) > 0)
					{
						Texture2D nextTile = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapTiles, tile, true);
						Rectangle? src = nextTile.Width > 64 ? new Rectangle?(new Rectangle((int)_tileSrc.X, (int)_tileSrc.Y, nextTile.Width / 4, nextTile.Height)) : null;
						if (nextTile.Width > 64)
							_sb.Draw(nextTile, new Vector2(pos.X - 1, pos.Y - 2), src, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(j, i, c)));
						else
							_sb.Draw(nextTile, new Vector2(pos.X - 1, pos.Y - 2), Color.FromNonPremultiplied(255, 255, 255, _getAlpha(j, i, c)));
					}
				}

				_sb.End();
			}
		}

		private void _drawMapItems()
		{
			Character c = OldWorld.Instance.MainPlayer.ActiveCharacter;
			
			// Queries (func) for the gfx items within range of the character's X coordinate
			Func<int, bool> queryX = x => x >= c.X - Constants.ViewLength && x <= c.X + Constants.ViewLength && x <= MapRef.Properties.Width;
			// Queries (func) for the gfx items within range of the character's Y coordinate
			Func<int, bool> queryY = y => y >= c.Y - Constants.ViewLength && y <= c.Y + Constants.ViewLength && y <= MapRef.Properties.Height;

			List<Point> keys = new List<Point>(_mapItems.Keys.Where(_key => queryX(_key.X) && queryY(_key.Y)));

			_sb.Begin();
			foreach (Point pt in keys)
			{
				//deep copies!
				List<MapItem> local = new List<MapItem>(_mapItems[pt]);
				foreach(MapItem item in local)
				{
					var itemData = OldWorld.Instance.EIF.GetRecordByID(item.ItemID);
					var itemPos = GetDrawCoordinatesFromGridUnits(item.X + 1, item.Y, c);
					var itemTexture = ChestDialog.GetItemGraphic(itemData, item.Amount);
					_sb.Draw(itemTexture, 
							 new Vector2(itemPos.X - (int)Math.Round(itemTexture.Width / 2.0),
										 itemPos.Y - (int)Math.Round(itemTexture.Height / 2.0)),
							Color.White);
					}
			}
			_sb.End();
		}

		private void _drawMapObjectsAndActors()
		{
			if (MapRef == null) return;

			Character c = OldWorld.Instance.MainPlayer.ActiveCharacter;

			List<CharacterRenderer> otherChars;
			lock (_characterListLock)
				otherChars = new List<CharacterRenderer>(_characterRenderers); //copy of list (can remove items)

			List<NPCRenderer> otherNpcs;
			lock(_npcListLock)
				otherNpcs = new List<NPCRenderer>(_npcRenderers);

			Dictionary<Point, Texture2D> drawRoofLater = new Dictionary<Point, Texture2D>();

			GraphicsDevice.SetRenderTarget(_rtMapObjAbovePlayer);
			GraphicsDevice.Clear(ClearOptions.Target, Color.Transparent, 0, 0);
			bool targetChanged = false;

			var firstRow = Math.Max(c.Y - 22, 0);
			int lastRow = Math.Min(c.Y + 22, MapRef.Properties.Height);
			var firstCol = Math.Max(c.X - 22, 0);
			int lastCol = Math.Min(c.X + 22, MapRef.Properties.Width);

			//no need to iterate over the entire map rows if they won't be included in the render.
			for (int rowIndex = firstRow; rowIndex <= lastRow; ++rowIndex)
			{
				_sb.Begin();
				var rowDelta = Math.Abs(c.Y - rowIndex);
				for (int colIndex = firstCol; colIndex <= lastCol; ++colIndex)
				{
					var colDelta = Math.Abs(c.X - colIndex);
					//once we hit the main players (x, y) coordinate, we need to switch render targets
					if (!targetChanged &&
						((c.State != CharacterActionState.Walking && rowIndex == c.Y && colIndex == c.X) ||
						 (c.State == CharacterActionState.Walking && rowIndex == c.DestY && colIndex == c.DestX)))
					{
						try
						{
							_sb.End();
							GraphicsDevice.SetRenderTarget(_rtMapObjBelowPlayer);
							GraphicsDevice.Clear(ClearOptions.Target, Color.Transparent, 0, 0);
							_sb.Begin();
						}
						catch (ObjectDisposedException)
						{
							return;
						}
						targetChanged = true;
					}

					//overlay and shadows: within 10 grid units
					if (colDelta <= 10 && rowDelta <= 10)
					{
						_drawOverlayAtLoc(rowIndex, colIndex, c);
						_drawShadowsAtLoc(rowIndex, colIndex, c);
					}

					if (colDelta <= 20 && rowDelta <= 20)
						_drawWallAtLoc(rowIndex, colIndex, c);

					_drawMapObjectsAtLoc(rowIndex, colIndex, c);

					if (rowDelta <= Constants.ViewLength && colDelta <= Constants.ViewLength)
						_drawCharactersAndNPCsAtLoc(rowIndex, colIndex, otherNpcs, otherChars);

					if (colDelta <= 12 && rowDelta <= 12)
					{
						_drawRoofsAtLoc(rowIndex, colIndex, drawRoofLater);
						_drawUnknownLayerAtLoc(rowIndex, colIndex, c);
						_drawOnTopLayerAtLoc(rowIndex, colIndex, c);
					}
				}

				try
				{
					_sb.End();
				}
				catch (InvalidOperationException)
				{
					_sb.Dispose();
					_sb = new SpriteBatch(Game.GraphicsDevice);
				}
			}

			_drawRoofsOnTop(drawRoofLater, c);

			_sb.Begin(SpriteSortMode.Deferred, OldWorld.Instance.MainPlayer.ActiveCharacter.RenderData.hidden ? BlendState.NonPremultiplied : _playerBlend);
			OldWorld.Instance.ActiveCharacterRenderer.Draw(_sb, true);
			_sb.End();

			GraphicsDevice.SetRenderTarget(null);
		}

		private void _drawOverlayAtLoc(int rowIndex, int colIndex, Character c)
		{
			int gfxNum;
			//overlay/mask  objects
			if ((gfxNum = MapRef.GFX[MapLayer.OverlayObjects][rowIndex, colIndex]) > 0)
			{
				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapOverlay, gfxNum, true);
				Vector2 pos = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);
				pos = new Vector2(pos.X + 16, pos.Y - 11);
				_sb.Draw(gfx, pos, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(colIndex, rowIndex, c)));
			}
		}

		private void _drawShadowsAtLoc(int rowIndex, int colIndex, Character c)
		{
			//shadows
			int gfxNum;
			if (OldWorld.Instance.ShowShadows && (gfxNum = MapRef.GFX[MapLayer.Shadow][rowIndex, colIndex]) > 0)
			{
				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.Shadows, gfxNum, true);
				Vector2 loc = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);
				_sb.Draw(gfx, new Vector2(loc.X - 24, loc.Y - 12), Color.FromNonPremultiplied(255, 255, 255, 60));
			}
		}

		private void _drawWallAtLoc(int rowIndex, int colIndex, Character c)
		{
			int gfxNum;
			const int WALL_FRAME_WIDTH = 68;
			//right-facing walls
			if ((gfxNum = MapRef.GFX[MapLayer.WallRowsRight][rowIndex, colIndex]) > 0)
			{
				if (_door != null && _door.x == colIndex && _doorY == rowIndex && _door.doorOpened)
					gfxNum++;

				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapWalls, gfxNum, true);
				Vector2 loc = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);

				int gfxWidthDelta = gfx.Width / 4;
				Rectangle? src = gfx.Width > WALL_FRAME_WIDTH
					? new Rectangle?(new Rectangle(gfxWidthDelta * _wallSrcIndex, 0, gfxWidthDelta, gfx.Height))
					: null;
				loc = new Vector2(loc.X - (int)Math.Round((gfx.Width > WALL_FRAME_WIDTH ? gfxWidthDelta : gfx.Width) / 2.0) + 47,
					loc.Y - (gfx.Height - 29));

				_sb.Draw(gfx, loc, src, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(colIndex, rowIndex, c)));
			}

			//down-facing walls
			if ((gfxNum = MapRef.GFX[MapLayer.WallRowsDown][rowIndex, colIndex]) > 0)
			{
				if (_door != null && _door.x == colIndex && _doorY == rowIndex && _door.doorOpened)
					gfxNum++;

				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapWalls, gfxNum, true);
				Vector2 loc = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);

				int gfxWidthDelta = gfx.Width / 4;
				Rectangle? src = gfx.Width > WALL_FRAME_WIDTH
					? new Rectangle?(new Rectangle(gfxWidthDelta * _wallSrcIndex, 0, gfxWidthDelta, gfx.Height))
					: null;
				loc = new Vector2(loc.X - (int)Math.Round((gfx.Width > WALL_FRAME_WIDTH ? gfxWidthDelta : gfx.Width) / 2.0) + 15,
					loc.Y - (gfx.Height - 29));

				_sb.Draw(gfx, loc, src, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(colIndex, rowIndex, c)));
			}
		}

		private void _drawMapObjectsAtLoc(int rowIndex, int colIndex, Character c)
		{
			int gfxNum;
			if ((gfxNum = MapRef.GFX[MapLayer.Objects][rowIndex, colIndex]) > 0)
			{
				bool shouldDrawObject = true;
				lock (_spikeTrapsLock)
				{
					if (_isSpikeGFX(gfxNum) &&
						MapRef.Tiles[rowIndex, colIndex] == TileSpec.SpikesTrap &&
						!_visibleSpikeTraps.Contains(new Point(colIndex, rowIndex)))
						shouldDrawObject = false;
				}

				if (shouldDrawObject)
				{
					var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapObjects, gfxNum, true);
					Vector2 loc = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);
					loc = new Vector2(loc.X - (int)Math.Round(gfx.Width / 2.0) + 29, loc.Y - (gfx.Height - 28));
					_sb.Draw(gfx, loc, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(colIndex, rowIndex, c)));
				}
			}
		}

		private void _drawCharactersAndNPCsAtLoc(int rowIndex, int colIndex, List<NPCRenderer> otherNpcs, List<CharacterRenderer> otherChars)
		{
			var thisLocNpcs = otherNpcs.Where(_npc => (_npc.NPC.Walking ? _npc.NPC.DestY == rowIndex : _npc.NPC.Y == rowIndex) &&
													  (_npc.NPC.Walking ? _npc.NPC.DestX == colIndex : _npc.NPC.X == colIndex)).ToList();
			thisLocNpcs.ForEach(npc => npc.DrawToSpriteBatch(_sb, true));

			var thisLocChars = otherChars.Where(_char => _char.Character.State == CharacterActionState.Walking
														? _char.Character.DestY == rowIndex && _char.Character.DestX == colIndex
														: _char.Character.Y == rowIndex && _char.Character.X == colIndex).ToList();
			thisLocChars.ForEach(@char => @char.Draw(_sb, true));
		}

		private void _drawRoofsAtLoc(int rowIndex, int colIndex, Dictionary<Point, Texture2D> drawRoofLater)
		{
			int gfxNum;
			//roofs (after objects - for outdoor maps, which actually have roofs, this makes more sense)
			if ((gfxNum = MapRef.GFX[MapLayer.Roof][rowIndex, colIndex]) > 0)
			{
				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapOverlay, gfxNum, true);
				drawRoofLater.Add(new Point(colIndex, rowIndex), gfx);
			}
		}

		private void _drawUnknownLayerAtLoc(int rowIndex, int colIndex, Character c)
		{
			int gfxNum;
			if ((gfxNum = MapRef.GFX[MapLayer.Unknown][rowIndex, colIndex]) > 0)
			{
				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapWallTop, gfxNum, true);
				Vector2 loc = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);
				loc = new Vector2(loc.X, loc.Y - 65);
				_sb.Draw(gfx, loc, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(colIndex, rowIndex, c)));
			}
		}

		private void _drawOnTopLayerAtLoc(int rowIndex, int colIndex, Character c)
		{
			int gfxNum;
			//overlay tiles (counters, etc)
			if ((gfxNum = MapRef.GFX[MapLayer.OverlayTile][rowIndex, colIndex]) > 0)
			{
				var gfx = EOGame.Instance.GFXManager.TextureFromResource(GFXTypes.MapTiles, gfxNum, true);
				Vector2 loc = GetDrawCoordinatesFromGridUnits(colIndex, rowIndex, c);
				loc = new Vector2(loc.X - 2, loc.Y - 31);
				_sb.Draw(gfx, loc, Color.White);
			}
		}

		private void _drawRoofsOnTop(Dictionary<Point, Texture2D> drawRoofLater, Character c)
		{
			_sb.Begin();

			foreach (var kvp in drawRoofLater)
			{
				Vector2 loc = GetDrawCoordinatesFromGridUnits(kvp.Key.X, kvp.Key.Y, c);
				loc = new Vector2(loc.X - kvp.Value.Width/2f + 30, loc.Y - kvp.Value.Height + 28);
				_sb.Draw(kvp.Value, loc, Color.FromNonPremultiplied(255, 255, 255, _getAlpha(kvp.Key.X, kvp.Key.Y, c)));
			}

			try
			{
				_sb.End();
			}
			catch (InvalidOperationException)
			{
				_sb.Dispose();
				_sb = new SpriteBatch(Game.GraphicsDevice);
			}
		}

		private void _renderSpellOnPlayer(short spellID, CharacterRenderer renderer)
		{
			if (spellID < 1) return;

			var spellInfo = OldWorld.Instance.ESF.GetRecordByID(spellID);
			renderer.ShowSpellAnimation(spellInfo.Graphic);
		}

		private void _renderSpellOnNPC(short spellID, NPCRenderer renderer)
		{
			if (spellID < 1) return;

			var spellInfo = OldWorld.Instance.ESF.GetRecordByID(spellID);
			renderer.ShowSpellAnimation(spellInfo.Graphic);
		}

		/// <summary>
		/// does the offset for tiles/items
		/// <para>(x * 32 - y * 32 + 288 - c.OffsetX), (y * 16 + x * 16 + 144 - c.OffsetY)</para>
		/// <para>Additional offsets for some gfx will need to be made - this Vector2 is a starting point with calculations required for ALL gfx</para>
		/// </summary>
		public static Vector2 GetDrawCoordinatesFromGridUnits(int x, int y, Character c)
		{
			return GetDrawCoordinatesFromGridUnits(x, y, c.OffsetX, c.OffsetY);
		}

		private static Vector2 GetDrawCoordinatesFromGridUnits(int x, int y, int cOffX, int cOffY)
		{
			return new Vector2((x * 32) - (y * 32) + 288 - cOffX, (y * 16) + (x * 16) + 144 - cOffY);
		}
		
		private int _getAlpha(int objX, int objY, Character c)
		{
			if (!OldWorld.Instance.ShowTransition)
				return 255;

			//get greater of deltas between the map object and the character
			int metric = Math.Max(Math.Abs(objX - c.X), Math.Abs(objY - c.Y));
			const double TRANSITION_TIME_MS = 125.0; //1/8 second for transition on each tile metric

			int alpha;
			if (_mapLoadTime == null || metric < _transitionMetric || metric == 0)
				alpha = 255;
			else if (metric == _transitionMetric)
			{
				double ms = (DateTime.Now - _mapLoadTime.Value).TotalMilliseconds;
				alpha = (int)Math.Round((ms / TRANSITION_TIME_MS) * 255);
				if (ms / TRANSITION_TIME_MS >= 1)
				{
					_mapLoadTime = DateTime.Now;
					_transitionMetric++;
				}
			}
			else
				alpha = 0;

			return alpha;
		}

		private bool _isSpikeGFX(int gfxNum)
		{
			return Constants.TrapSpikeGFXObjectIDs.Contains(gfxNum);
		}

		#endregion

		/* DISPOSABLE INTERFACE OVERRIDES AND STUFF */
		public new void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected override void Dispose(bool disposing)
		{
			if (_disposed) return;

			_drawingEvent.Wait();

			lock (_disposingLockObject)
			{
				if (!disposing)
				{
					base.Dispose(false);
					return;
				}

				lock (_characterListLock)
					foreach (CharacterRenderer cr in _characterRenderers)
						cr.Dispose();

				lock (_npcListLock)
				{
					foreach (NPCRenderer npc in _npcRenderers)
						npc.Dispose();
				}

				if (_rtMapObjAbovePlayer != null)
					_rtMapObjAbovePlayer.Dispose();
				if (_rtMapObjBelowPlayer != null)
					_rtMapObjBelowPlayer.Dispose();
				if (_playerBlend != null)
					_playerBlend.Dispose();
				_sb.Dispose();
				_doorTimer.Dispose();

				if(_miniMapRenderer != null)
					_miniMapRenderer.Dispose();

				if (_mouseCursorRenderer != null)
					_mouseCursorRenderer.Dispose();

				base.Dispose(true);
				_disposed = true;

				_drawingEvent.Dispose();
				_drawingEvent = null;
			}
		}
	}
}
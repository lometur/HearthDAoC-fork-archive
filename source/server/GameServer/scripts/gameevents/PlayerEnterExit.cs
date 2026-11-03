using System;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS.GameEvents
{
	/// <summary>
	/// Spams everyone online with player enter/exit messages
	/// </summary>
	public class PlayerEnterExit
	{
		/// <summary>
		/// Event handler fired when server is started
		/// </summary>
		[GameServerStartedEvent]
		public static void OnServerStart(DOLEvent e, object sender, EventArgs arguments)
		{
			GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(PlayerEntered));
			GameEventMgr.AddHandler(GamePlayerEvent.Quit, new DOLEventHandler(PlayerQuit));
		}

		/// <summary>
		/// Event handler fired when server is stopped
		/// </summary>
		[GameServerStoppedEvent]
		public static void OnServerStop(DOLEvent e, object sender, EventArgs arguments)
		{
			GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(PlayerEntered));
			GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, new DOLEventHandler(PlayerQuit));
		}

		// HearthDAoC: who of the players online is told that a player logged in or out (#160; the owner,
		// 2026-11-03: "the bots should only do local messaging", "'/region' is fine"). Nobody with show_logins
		// off or for an anonymous player, and never the player itself. A human's go to every player online, as
		// before. A player bot's go to the players its /region reaches (RegionCommandHandler.Broadcast): those
		// in its region whom the server rules let understand it; a bot with no region tells nobody. Every quit
		// (the quit timer's end, a link death's end, a kick to the character screen, a dropped client) raises
		// Quit in GamePlayer.Quit(true), before Delete and with CurrentRegion still set, so a bot's log-out
		// goes to the region it is in (only a collapsing instance clears it, and then nobody is told).
		public static System.Collections.Generic.IEnumerable<GamePlayer> Audience(GamePlayer player,
			System.Collections.Generic.IEnumerable<GamePlayer> online)
		{
			if (!ServerProperties.Properties.SHOW_LOGINS || player.IsAnonymous)
				yield break;

			bool bot = HearthDAoC.PlayerBots.PlayerBotPlan.IsBotAccount(player.Client?.Account?.Name);
			Region region = player.CurrentRegion;

			if (bot && region == null)
				yield break;

			foreach (GamePlayer otherPlayer in online)
			{
				if (otherPlayer == player)
					continue;

				if (bot && (otherPlayer.CurrentRegion != region ||
					!GameServer.ServerRules.IsAllowedToUnderstand(otherPlayer, player)))
					continue;

				yield return otherPlayer;
			}
		}

		/// <summary>
		/// Event handler fired when players enters the game
		/// </summary>
		/// <param name="e"></param>
		/// <param name="sender"></param>
		/// <param name="arguments"></param>
		private static void PlayerEntered(DOLEvent e, object sender, EventArgs arguments)
		{
			// HearthDAoC: the players Audience picks are told (#160). It holds show_logins, IsAnonymous and
			// the player itself, and keeps a player bot's to its region.
			if (sender is not GamePlayer player)
				return;

			foreach (GamePlayer otherPlayer in Audience(player, ClientService.Instance.GetPlayers()))
			{
				string message = LanguageMgr.GetTranslation(otherPlayer.Client, "Scripts.Events.PlayerEnterExit.Entered", player.Name);

				if (player.Client.Account.PrivLevel > 1)
				{
					message = LanguageMgr.GetTranslation(otherPlayer.Client, "Scripts.Events.PlayerEnterExit.Staff", message);
				}
				else
				{
					string realm = string.Empty;
					if (GameServer.Instance.Configuration.ServerType == EGameServerType.GST_Normal)
					{
						realm = "[" + GlobalConstants.RealmToName(player.Realm) + "] ";
					}
					message = realm + message;
				}

				eChatType chatType = eChatType.CT_System;

				if (Enum.IsDefined(typeof(eChatType), ServerProperties.Properties.SHOW_LOGINS_CHANNEL))
					chatType = (eChatType)ServerProperties.Properties.SHOW_LOGINS_CHANNEL;

				otherPlayer.Out.SendMessage(message, chatType, eChatLoc.CL_SystemWindow);
			}
		}

		/// <summary>
		/// Event handler fired when player leaves the game
		/// </summary>
		/// <param name="e"></param>
		/// <param name="sender"></param>
		/// <param name="arguments"></param>
		private static void PlayerQuit(DOLEvent e, object sender, EventArgs arguments)
		{
			// HearthDAoC: the players Audience picks are told (#160). It holds show_logins, IsAnonymous and
			// the player itself, and keeps a player bot's to its region.
			GamePlayer player = sender as GamePlayer;
			if (player == null) return;

			foreach (GamePlayer otherPlayer in Audience(player, ClientService.Instance.GetPlayers()))
			{
				string message = LanguageMgr.GetTranslation(otherPlayer.Client, "Scripts.Events.PlayerEnterExit.Left", player.Name);

				if (player.Client.Account.PrivLevel > 1)
				{
					message = LanguageMgr.GetTranslation(otherPlayer.Client, "Scripts.Events.PlayerEnterExit.Staff", message);
				}
				else
				{
					string realm = string.Empty;
					if (GameServer.Instance.Configuration.ServerType == EGameServerType.GST_Normal)
					{
						realm = "[" + GlobalConstants.RealmToName(player.Realm) + "] ";
					}
					message = realm + message;
				}

				eChatType chatType = eChatType.CT_System;

				if (Enum.IsDefined(typeof(eChatType), ServerProperties.Properties.SHOW_LOGINS_CHANNEL))
					chatType = (eChatType)ServerProperties.Properties.SHOW_LOGINS_CHANNEL;

				otherPlayer.Out.SendMessage(message, chatType, eChatLoc.CL_SystemWindow);
			}
		}
	}
}

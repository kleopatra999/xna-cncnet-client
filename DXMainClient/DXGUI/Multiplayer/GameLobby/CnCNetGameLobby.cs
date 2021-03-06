﻿using ClientCore;
using DTAClient.Domain.Multiplayer;
using DTAClient.Domain.Multiplayer.CnCNet;
using DTAClient.DXGUI.Generic;
using DTAClient.DXGUI.Multiplayer.GameLobby.CommandHandlers;
using DTAClient.Online;
using DTAClient.Online.EventArguments;
using Microsoft.Xna.Framework;
using Rampastring.Tools;
using Rampastring.XNAUI;
using Rampastring.XNAUI.XNAControls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DTAClient.DXGUI.Multiplayer.GameLobby
{
    public class CnCNetGameLobby : MultiplayerGameLobby
    {
        private const double GAME_BROADCAST_INTERVAL = 30.0;
        private const double GAME_BROADCAST_ACCELERATION = 10.0;
        private const double INITIAL_GAME_BROADCAST_DELAY = 10.0;

        private const string MAP_SHARING_FAIL_MESSAGE = "MAPFAIL";
        private const string MAP_SHARING_DOWNLOAD_REQUEST = "MAPOK";
        private const string MAP_SHARING_UPLOAD_REQUEST = "MAPREQ";
        private const string MAP_SHARING_DISABLED_MESSAGE = "MAPSDISABLED";
        private const string FRAME_SEND_RATE_MESSAGE = "SFSR";
        private const string CHEAT_DETECTED_MESSAGE = "CD";

        public CnCNetGameLobby(WindowManager windowManager, string iniName, 
            TopBar topBar, List<GameMode> GameModes, CnCNetManager connectionManager,
            TunnelHandler tunnelHandler) : 
            base(windowManager, iniName, topBar, GameModes)
        {
            this.connectionManager = connectionManager;
            localGame = ClientConfiguration.Instance.LocalGame;
            this.tunnelHandler = tunnelHandler;

            ctcpCommandHandlers = new CommandHandlerBase[]
            {
                new IntCommandHandler("OR", new Action<string, int>(HandleOptionsRequest)),
                new IntCommandHandler("R", new Action<string, int>(HandleReadyRequest)),
                new StringCommandHandler("PO", new Action<string, string>(ApplyPlayerOptions)),
                new StringCommandHandler("GO", new Action<string, string>(ApplyGameOptions)),
                new StringCommandHandler("START", new Action<string, string>(NonHostLaunchGame)),
                new NotificationHandler("AISPECS", HandleNotification, AISpectatorsNotification),
                new NotificationHandler("GETREADY", HandleNotification, GetReadyNotification),
                new NotificationHandler("INSFSPLRS", HandleNotification, InsufficientPlayersNotification),
                new NotificationHandler("TMPLRS", HandleNotification, TooManyPlayersNotification),
                new NotificationHandler("CLRS", HandleNotification, SharedColorsNotification),
                new NotificationHandler("SLOC", HandleNotification, SharedStartingLocationNotification),
                new NotificationHandler("LCKGME", HandleNotification, LockGameNotification),
                new IntNotificationHandler("NVRFY", HandleIntNotification, NotVerifiedNotification),
                new IntNotificationHandler("INGM", HandleIntNotification, StillInGameNotification),
                new StringCommandHandler(MAP_SHARING_UPLOAD_REQUEST, HandleMapUploadRequest),
                new StringCommandHandler(MAP_SHARING_FAIL_MESSAGE, HandleMapTransferFailMessage),
                new StringCommandHandler(MAP_SHARING_DOWNLOAD_REQUEST, HandleMapDownloadRequest),
                new NoParamCommandHandler(MAP_SHARING_DISABLED_MESSAGE, HandleMapSharingBlockedMessage),
                new NoParamCommandHandler("RETURN", ReturnNotification),
                new IntCommandHandler("TNLPNG", TunnelPingNotification),
                new StringCommandHandler("FHSH", FileHashNotification),
                new StringCommandHandler("MM", CheaterNotification),
                new IntCommandHandler(FRAME_SEND_RATE_MESSAGE, SetFrameSendRateForNonHostPlayer),
                new NoParamCommandHandler(CHEAT_DETECTED_MESSAGE, HandleCheatDetectedMessage),
            };

            MapSharer.MapDownloadFailed += MapSharer_MapDownloadFailed;
            MapSharer.MapDownloadComplete += MapSharer_MapDownloadComplete;
            MapSharer.MapUploadFailed += MapSharer_MapUploadFailed;
            MapSharer.MapUploadComplete += MapSharer_MapUploadComplete;
        }

        public event EventHandler GameLeft;

        private TunnelHandler tunnelHandler;
        private CnCNetTunnel tunnel;

        private Channel channel;
        private CnCNetManager connectionManager;
        private string localGame;

        private string hostName;

        private CommandHandlerBase[] ctcpCommandHandlers;

        private IRCColor chatColor;

        private XNATimerControl gameBroadcastTimer;

        private int playerLimit;

        private bool closed = false;

        private bool isCustomPassword = false;

        private string gameFilesHash;

        private List<string> hostUploadedMaps = new List<string>();

        /// <summary>
        /// The SHA1 of the latest selected map.
        /// Used for map sharing.
        /// </summary>
        private string lastMapSHA1;

        /// <summary>
        /// The game mode of the latest selected map.
        /// Used for map sharing.
        /// </summary>
        private string lastGameMode;

        public override void Initialize()
        {
            base.Initialize();

            gameBroadcastTimer = new XNATimerControl(WindowManager);
            gameBroadcastTimer.AutoReset = true;
            gameBroadcastTimer.Interval = TimeSpan.FromSeconds(GAME_BROADCAST_INTERVAL);
            gameBroadcastTimer.Enabled = false;
            gameBroadcastTimer.TimeElapsed += GameBroadcastTimer_TimeElapsed;

            WindowManager.AddAndInitializeControl(gameBroadcastTimer);
        }

        private void GameBroadcastTimer_TimeElapsed(object sender, EventArgs e)
        {
            BroadcastGame();
        }

        public void SetUp(Channel channel, bool isHost, int playerLimit, 
            CnCNetTunnel tunnel, string hostName, bool isCustomPassword)
        {
            this.channel = channel;
            channel.MessageAdded += Channel_MessageAdded;
            channel.CTCPReceived += Channel_CTCPReceived;
            channel.UserKicked += Channel_UserKicked;
            channel.UserQuitIRC += Channel_UserQuitIRC;
            channel.UserLeft += Channel_UserLeft;
            channel.UserAdded += Channel_UserAdded;

            this.hostName = hostName;
            this.playerLimit = playerLimit;
            this.isCustomPassword = isCustomPassword;

            if (isHost)
            {
                RandomSeed = new Random().Next();
                RefreshMapSelectionUI();
            }
            else
            {
                channel.ChannelModesChanged += Channel_ChannelModesChanged;
                AIPlayers.Clear();
            }

            this.tunnel = tunnel;

            connectionManager.ConnectionLost += ConnectionManager_ConnectionLost;
            connectionManager.Disconnected += ConnectionManager_Disconnected;

            Refresh(isHost);
        }

        public void OnJoined()
        {
            FileHashCalculator fhc = new FileHashCalculator();
            fhc.CalculateHashes(GameModes);

            gameFilesHash = fhc.GetCompleteHash();

            if (IsHost)
            {
                connectionManager.SendCustomMessage(new QueuedMessage(
                    string.Format("MODE {0} +klnNs {1} {2}", channel.ChannelName,
                    channel.Password, playerLimit),
                    QueuedMessageType.SYSTEM_MESSAGE, 50));

                connectionManager.SendCustomMessage(new QueuedMessage(
                    string.Format("TOPIC {0} :{1}", channel.ChannelName,
                    ProgramConstants.CNCNET_PROTOCOL_REVISION + ";" + localGame.ToLower()),
                    QueuedMessageType.SYSTEM_MESSAGE, 50));

                gameBroadcastTimer.Enabled = true;
                gameBroadcastTimer.Start();
                gameBroadcastTimer.SetTime(TimeSpan.FromSeconds(INITIAL_GAME_BROADCAST_DELAY));
            }
            else
            {
                channel.SendCTCPMessage("FHSH " + fhc.GetCompleteHash(), QueuedMessageType.SYSTEM_MESSAGE, 10);

                channel.SendCTCPMessage("TNLPNG " + tunnel.PingInMs, QueuedMessageType.SYSTEM_MESSAGE, 10);

                if (tunnel.PingInMs < 0)
                    AddNotice(ProgramConstants.PLAYERNAME + " - unknown ping to tunnel server.");
                else
                    AddNotice(ProgramConstants.PLAYERNAME + " - ping to tunnel server: " + tunnel.PingInMs + " ms");
            }

            TopBar.AddPrimarySwitchable(this);
            TopBar.SwitchToPrimary();
            WindowManager.SelectedControl = tbChatInput;
        }

        public void ChangeChatColor(IRCColor chatColor)
        {
            this.chatColor = chatColor;
            tbChatInput.TextColor = chatColor.XnaColor;
        }

        public override void Clear()
        {
            base.Clear();

            if (channel != null)
            {
                channel.MessageAdded -= Channel_MessageAdded;
                channel.CTCPReceived -= Channel_CTCPReceived;
                channel.UserKicked -= Channel_UserKicked;
                channel.UserQuitIRC -= Channel_UserQuitIRC;
                channel.UserLeft -= Channel_UserLeft;
                channel.UserAdded -= Channel_UserAdded;

                if (!IsHost)
                {
                    channel.ChannelModesChanged -= Channel_ChannelModesChanged;
                }
            }

            connectionManager.ConnectionLost -= ConnectionManager_ConnectionLost;
            connectionManager.Disconnected -= ConnectionManager_Disconnected;

            gameBroadcastTimer.Enabled = false;
            closed = false;

            tbChatInput.Text = string.Empty;

            GameLeft?.Invoke(this, EventArgs.Empty);

            TopBar.RemovePrimarySwitchable(this);
        }

        private void ConnectionManager_Disconnected(object sender, EventArgs e)
        {
            HandleConnectionLoss();
        }

        private void ConnectionManager_ConnectionLost(object sender, ConnectionLostEventArgs e)
        {
            HandleConnectionLoss();
        }

        private void HandleConnectionLoss()
        {
            Clear();
            this.Visible = false;
            this.Enabled = false;
        }

        protected override void BtnLeaveGame_LeftClick(object sender, EventArgs e)
        {
            if (IsHost)
            {
                closed = true;
                BroadcastGame();
            }

            Clear();
            channel.Leave();
            this.Visible = false;
            this.Enabled = false;
        }

        private void Channel_UserQuitIRC(object sender, UserNameIndexEventArgs e)
        {
            RemovePlayer(e.UserName);

            if (e.UserName == hostName)
            {
                connectionManager.MainChannel.AddMessage(new ChatMessage(
                    null, Color.Yellow, DateTime.Now, "The game host abandoned the game."));
                BtnLeaveGame_LeftClick(this, EventArgs.Empty);
            }
        }

        private void Channel_UserLeft(object sender, UserNameIndexEventArgs e)
        {
            RemovePlayer(e.UserName);

            if (e.UserName == hostName)
            {
                connectionManager.MainChannel.AddMessage(new ChatMessage(
                    null, Color.Yellow, DateTime.Now, "The game host abandoned the game."));
                BtnLeaveGame_LeftClick(this, EventArgs.Empty);
            }
        }

        private void Channel_UserKicked(object sender, UserNameIndexEventArgs e)
        {
            if (e.UserName == ProgramConstants.PLAYERNAME)
            {
                connectionManager.MainChannel.AddMessage(new ChatMessage(
                    null, Color.Yellow, DateTime.Now, "You were kicked from the game!"));
                Clear();
                this.Visible = false;
                this.Enabled = false;
                return;
            }

            int index = Players.FindIndex(p => p.Name == e.UserName);

            if (index > -1)
            {
                Players.RemoveAt(index);
                CopyPlayerDataToUI();
                ClearReadyStatuses();
            }
        }

        private void Channel_UserAdded(object sender, ChannelUserEventArgs e)
        {
            PlayerInfo pInfo = new PlayerInfo(e.User.IRCUser.Name);
            Players.Add(pInfo);

            if (Players.Count + AIPlayers.Count > MAX_PLAYER_COUNT && AIPlayers.Count > 0)
                AIPlayers.RemoveAt(AIPlayers.Count - 1);

            sndJoinSound.Play();

            if (!IsHost)
            {
                CopyPlayerDataToUI();
                return;
            }

            if (e.User.IRCUser.Name != ProgramConstants.PLAYERNAME)
            {
                // Changing the map applies forced settings (co-op sides etc.) to the
                // new player, and it also sends an options broadcast message
                CopyPlayerDataToUI();
                ChangeMap(GameMode, Map);
                BroadcastPlayerOptions();
            }
            else
            {
                Players[0].Ready = true;
                CopyPlayerDataToUI();
            }

            if (Players.Count >= playerLimit)
            {
                AddNotice("Player limit reached; the game room has been locked.");
                LockGame();
            }
        }

        private void RemovePlayer(string playerName)
        {
            PlayerInfo pInfo = Players.Find(p => p.Name == playerName);

            if (pInfo != null)
            {
                Players.Remove(pInfo);

                CopyPlayerDataToUI();
                BroadcastPlayerOptions();
            }

            sndLeaveSound.Play();

            if (IsHost && Locked && !ProgramConstants.IsInGame)
            {
                UnlockGame(true);
            }
        }

        private void Channel_ChannelModesChanged(object sender, ChannelModeEventArgs e)
        {
            if (e.ModeString == "+i")
            {
                if (Players.Count >= playerLimit)
                    AddNotice("Player limit reached; the game room has been locked.");
                else
                    AddNotice("The game host has locked the game room.");
            }
            else if (e.ModeString == "-i")
                AddNotice("The game room has been unlocked.");
        }

        private void Channel_CTCPReceived(object sender, ChannelCTCPEventArgs e)
        {
            Logger.Log("CnCNetGameLobby_CTCPReceived");

            foreach (CommandHandlerBase cmdHandler in ctcpCommandHandlers)
            {
                if (cmdHandler.Handle(e.UserName, e.Message))
                    return;
            }

            Logger.Log("Unhandled CTCP command: " + e.Message + " from " + e.UserName);
        }

        private void Channel_MessageAdded(object sender, IRCMessageEventArgs e)
        {
            lbChatMessages.AddMessage(e.Message);

            if (e.Message.Sender != null)
                sndMessageSound.Play();
        }

        /// <summary>
        /// Starts the game for the game host.
        /// </summary>
        protected override void HostLaunchGame()
        {
            if (Players.Count > 1)
            {
                AddNotice("Contacting tunnel server..");

                List<int> playerPorts = tunnel.GetPlayerPortInfo(Players.Count);

                if (playerPorts.Count < Players.Count)
                {
                    AddNotice("An error occured while contacting the specified CnCNet tunnel server. Please try using a different tunnel server " +
                        "(accessible through the advanced options in the game creation window).", Color.Yellow);
                    return;
                }

                StringBuilder sb = new StringBuilder("START ");
                sb.Append(UniqueGameID);
                for (int pId = 0; pId < Players.Count; pId++)
                {
                    Players[pId].Port = playerPorts[pId];
                    sb.Append(";");
                    sb.Append(Players[pId].Name);
                    sb.Append(";");
                    sb.Append("0.0.0.0:");
                    sb.Append(playerPorts[pId]);
                }
                channel.SendCTCPMessage(sb.ToString(), QueuedMessageType.SYSTEM_MESSAGE, 10);
            }
            else
            {
                Logger.Log("One player MP -- starting!");
            }

            Players.ForEach(pInfo => pInfo.IsInGame = true);

            StartGame();
        }

        protected override void RequestPlayerOptions(int side, int color, int start, int team)
        {
            byte[] value = new byte[]
            {
                (byte)side,
                (byte)color,
                (byte)start,
                (byte)team
            };

            int intValue = BitConverter.ToInt32(value, 0);

            channel.SendCTCPMessage(
                string.Format("OR {0}", intValue),
                QueuedMessageType.GAME_SETTINGS_MESSAGE, 6);
        }

        protected override void RequestReadyStatus()
        {
            if (Map == null || GameMode == null)
            {
                AddNotice("The game host needs to select a different map or " + 
                    "you will be unable to participate in the match.");
                return;
            }

            channel.SendCTCPMessage("R 1", QueuedMessageType.GAME_PLAYERS_READY_STATUS_MESSAGE, 5);
        }

        protected override void AddNotice(string message, Color color)
        {
            channel.AddMessage(new ChatMessage(null, color, DateTime.Now, message));
        }

        /// <summary>
        /// Handles player option requests received from non-host players.
        /// </summary>
        private void HandleOptionsRequest(string playerName, int options)
        {
            if (!IsHost)
                return;

            PlayerInfo pInfo = Players.Find(p => p.Name == playerName);

            if (pInfo == null)
                return;

            byte[] bytes = BitConverter.GetBytes(options);

            int side = bytes[0];
            int color = bytes[1];
            int start = bytes[2];
            int team = bytes[3];

            if (side < 0 || side > SideCount + 1)
                return;

            if (color < 0 || color > MPColors.Count)
                return;

            if (Map.CoopInfo != null)
            {
                if (Map.CoopInfo.DisallowedPlayerSides.Contains(side - 1) || side == SideCount + 1)
                    return;

                if (Map.CoopInfo.DisallowedPlayerColors.Contains(color - 1))
                    return;
            }

            if (start < 0 || start > Map.MaxPlayers)
                return;

            if (team < 0 || team > 4)
                return;

            if (side != pInfo.SideId 
                || start != pInfo.StartingLocation 
                || team != pInfo.TeamId)
            {
                ClearReadyStatuses();
            }

            pInfo.SideId = side;
            pInfo.ColorId = color;
            pInfo.StartingLocation = start;
            pInfo.TeamId = team;

            CopyPlayerDataToUI();
            BroadcastPlayerOptions();
        }

        /// <summary>
        /// Handles "I'm ready" messages received from non-host players.
        /// </summary>
        private void HandleReadyRequest(string playerName, int readyStatus)
        {
            if (!IsHost)
                return;

            PlayerInfo pInfo = Players.Find(p => p.Name == playerName);

            if (pInfo == null)
                return;

            pInfo.Ready = readyStatus > 0;

            CopyPlayerDataToUI();
            BroadcastPlayerOptions();
        }

        /// <summary>
        /// Broadcasts player options to non-host players.
        /// </summary>
        protected override void BroadcastPlayerOptions()
        {
            // Broadcast player options
            StringBuilder sb = new StringBuilder("PO ");
            foreach (PlayerInfo pInfo in Players.Concat(AIPlayers))
            {
                if (pInfo.IsAI)
                    sb.Append(pInfo.AILevel);
                else
                    sb.Append(pInfo.Name);
                sb.Append(";");

                // Combine the options into one integer to save bandwidth in
                // cases where the player uses default options (this is common for AI players)
                // Will hopefully make GameSurge kicking people a bit less common
                byte[] byteArray = new byte[]
                {
                    (byte)pInfo.TeamId,
                    (byte)pInfo.StartingLocation,
                    (byte)pInfo.ColorId,
                    (byte)pInfo.SideId,
                };

                int value = BitConverter.ToInt32(byteArray, 0);
                sb.Append(value);
                sb.Append(";");
                if (!pInfo.IsAI)
                {
                    sb.Append(Convert.ToInt32(pInfo.Ready));
                    sb.Append(';');
                }
            }

            channel.SendCTCPMessage(sb.ToString(), QueuedMessageType.GAME_PLAYERS_MESSAGE, 11);
        }

        /// <summary>
        /// Handles player option messages received from the game host.
        /// </summary>
        private void ApplyPlayerOptions(string sender, string message)
        {
            if (sender != hostName)
                return;

            Players.Clear();
            AIPlayers.Clear();

            string[] parts = message.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                PlayerInfo pInfo = new PlayerInfo();

                string pName = parts[i];
                int converted = Conversions.IntFromString(pName, -1);

                if (converted > -1)
                {
                    pInfo.IsAI = true;
                    pInfo.AILevel = converted;
                    pInfo.Name = AILevelToName(converted);
                }
                else
                    pInfo.Name = pName;

                if (parts.Length <= i + 1)
                {
                    return;
                }

                int playerOptions = Conversions.IntFromString(parts[i + 1], -1);
                if (playerOptions == -1)
                    return;

                byte[] byteArray = BitConverter.GetBytes(playerOptions);

                int team = byteArray[0];
                int start = byteArray[1];
                int color = byteArray[2];
                int side = byteArray[3];

                if (side < 0 || side > SideCount + 1)
                    return;

                if (color < 0 || color > MPColors.Count)
                    return;

                if (start < 0 || start > MAX_PLAYER_COUNT)
                    return;

                if (team < 0 || team > 4)
                    return;

                pInfo.TeamId = byteArray[0];
                pInfo.StartingLocation = byteArray[1];
                pInfo.ColorId = byteArray[2];
                pInfo.SideId = byteArray[3];

                if (pInfo.IsAI)
                {
                    pInfo.Ready = true;
                    AIPlayers.Add(pInfo);
                    i++;
                }
                else
                {
                    if (parts.Length <= i + 2)
                        return;

                    int readyStatus = Conversions.IntFromString(parts[i + 2], -1);

                    if (readyStatus == -1)
                        return;

                    pInfo.Ready = readyStatus > 0;

                    Players.Add(pInfo);
                    i += 2;
                }
            }

            CopyPlayerDataToUI();
        }

        /// <summary>
        /// Broadcasts game options to non-host players
        /// when the host has changed an option.
        /// </summary>
        protected override void OnGameOptionChanged()
        {
            base.OnGameOptionChanged();

            if (!IsHost)
                return;

            bool[] optionValues = new bool[CheckBoxes.Count];
            for (int i = 0; i < CheckBoxes.Count; i++)
                optionValues[i] = CheckBoxes[i].Checked;

            // Let's pack the booleans into bytes
            List<byte> byteList = Conversions.BoolArrayIntoBytes(optionValues).ToList();
            
            while (byteList.Count % 4 != 0)
            {
                byteList.Add(0);
            }

            int integerCount = byteList.Count / 4;
            byte[] byteArray = byteList.ToArray();

            StringBuilder sb = new StringBuilder("GO ");

            for (int i = 0; i < integerCount; i++)
            {
                sb.Append(BitConverter.ToInt32(byteArray, i * 4));
                sb.Append(";");
            }

            // We don't gain much in most cases by packing the drop-down values
            // (because they're bytes to begin with, and usually non-zero),
            // so let's just transfer them as usual

            foreach (GameLobbyDropDown dd in DropDowns)
            {
                sb.Append(dd.SelectedIndex);
                sb.Append(";");
            }

            sb.Append(Convert.ToInt32(Map.Official));
            sb.Append(';');
            sb.Append(Map.SHA1);
            sb.Append(";");
            sb.Append(GameMode.Name);
            sb.Append(";");
            sb.Append(RandomSeed);

            channel.SendCTCPMessage(sb.ToString(), QueuedMessageType.GAME_SETTINGS_MESSAGE, 11);
        }

        /// <summary>
        /// Handles game option messages received from the game host.
        /// </summary>
        private void ApplyGameOptions(string sender, string message)
        {
            if (sender != hostName)
                return;

            string[] parts = message.Split(';');

            int checkBoxIntegerCount = (CheckBoxes.Count / 32) + 1;

            int partIndex = checkBoxIntegerCount + DropDowns.Count;

            if (parts.Length < partIndex + 3)
                return;

            string mapOfficial = parts[partIndex];
            bool isMapOfficial = Conversions.BooleanFromString(mapOfficial, true);

            string mapSHA1 = parts[partIndex + 1];

            string gameMode = parts[partIndex + 2];

            GameMode currentGameMode = GameMode;
            Map currentMap = Map;

            lastGameMode = gameMode;
            lastMapSHA1 = mapSHA1;

            GameMode = GameModes.Find(gm => gm.Name == gameMode);
            if (GameMode == null)
            {
                if (!isMapOfficial)
                    RequestMap(mapSHA1);
                else
                    AddOfficialMapMissingMessage(mapSHA1);

                return;
            }

            Map = GameMode.Maps.Find(map => map.SHA1 == mapSHA1);

            if (Map == null)
            {
                if (!isMapOfficial)
                    RequestMap(mapSHA1);
                else
                    AddOfficialMapMissingMessage(mapSHA1);

                return;
            }

            if (GameMode != currentGameMode || Map != currentMap)
                ChangeMap(GameMode, Map);

            // By changing the game options after changing the map,
            // we know which game options were changed by the map
            // and which were changed by the game host

            for (int i = 0; i < checkBoxIntegerCount; i++)
            {
                if (parts.Length <= i)
                    return;

                int checkBoxStatusInt;
                bool success = int.TryParse(parts[i], out checkBoxStatusInt);

                if (!success)
                    return;

                byte[] byteArray = BitConverter.GetBytes(checkBoxStatusInt);
                bool[] boolArray = Conversions.BytesIntoBoolArray(byteArray);

                for (int optionIndex = 0; optionIndex < boolArray.Length; optionIndex++)
                {
                    int gameOptionIndex = i * 32 + optionIndex;

                    if (gameOptionIndex >= CheckBoxes.Count)
                        break;

                    GameLobbyCheckBox checkBox = CheckBoxes[gameOptionIndex];

                    if (checkBox.Checked != boolArray[optionIndex])
                    {
                        if (boolArray[optionIndex])
                            AddNotice("The game host has enabled " + checkBox.Text);
                        else
                            AddNotice("The game host has disabled " + checkBox.Text);
                    }

                    CheckBoxes[gameOptionIndex].Checked = boolArray[optionIndex];
                }
            }

            for (int i = checkBoxIntegerCount; i < DropDowns.Count + checkBoxIntegerCount; i++)
            {
                if (parts.Length <= i)
                    return;

                int ddSelectedIndex;
                bool success = int.TryParse(parts[i], out ddSelectedIndex);

                if (!success)
                    return;

                GameLobbyDropDown dd = DropDowns[i - checkBoxIntegerCount];

                if (ddSelectedIndex < 0 || ddSelectedIndex >= dd.Items.Count)
                    return;

                if (dd.SelectedIndex != ddSelectedIndex)
                {
                    string ddName = dd.OptionName;
                    if (dd.OptionName == null)
                        ddName = dd.Name;

                    AddNotice("The game host has set " + ddName + " to " + dd.Items[ddSelectedIndex].Text);
                }

                DropDowns[i - checkBoxIntegerCount].SelectedIndex = ddSelectedIndex;
            }

            int randomSeed;
            bool parseSuccess = int.TryParse(parts[partIndex + 3], out randomSeed);

            if (!parseSuccess)
                return;

            RandomSeed = randomSeed;
        }

        private void RequestMap(string mapSHA1)
        {
            ChangeMap(null, null);
            if (UserINISettings.Instance.EnableMapSharing)
            {
                AddNotice("The game host has selected a map that doesn't exist on your installation. " +
                    "Attempting to download it from the CnCNet map database.");
                MapSharer.DownloadMap(mapSHA1, localGame);
            }
            else
            {
                AddNotice("The game host has selected a map that doesn't exist on your installation. " +
                    "Because you've disabled map sharing, it cannot be transferred. The game host needs " +
                    "to change the map or you will be unable to participate in the match.");
                channel.SendCTCPMessage(MAP_SHARING_DISABLED_MESSAGE, QueuedMessageType.SYSTEM_MESSAGE, 9);
            }
        }

        private void AddOfficialMapMissingMessage(string sha1)
        {
            AddNotice("The game host has selected an official map that doesn't exist on your installation." +
                "This could mean that the game host has modified game files, or is running a different game version." +
                "They need to change the map or you will be unable to participate in the match.");
            channel.SendCTCPMessage(MAP_SHARING_FAIL_MESSAGE + " " + sha1, QueuedMessageType.SYSTEM_MESSAGE, 9);
        }

        /// <summary>
        /// Signals other players that the local player has returned from the game,
        /// and unlocks the game as well as generates a new random seed as the game host.
        /// </summary>
        protected override void GameProcessExited()
        {
            base.GameProcessExited();

            channel.SendCTCPMessage("RETURN", QueuedMessageType.SYSTEM_MESSAGE, 20);
            ReturnNotification(ProgramConstants.PLAYERNAME);

            if (IsHost)
            {
                RandomSeed = new Random().Next();
                OnGameOptionChanged();
                ClearReadyStatuses();
                CopyPlayerDataToUI();
                BroadcastPlayerOptions();

                if (Players.Count < playerLimit)
                {
                    UnlockGame(true);
                }
            }
        }

        /// <summary>
        /// Handles the "START" (game start) command sent by the game host.  
        /// </summary>
        private void NonHostLaunchGame(string sender, string message)
        {
            if (sender != hostName)
                return;

            string[] parts = message.Split(';');

            if (parts.Length < 1)
                return;

            UniqueGameID = Conversions.IntFromString(parts[0], -1);
            if (UniqueGameID < 0)
                return;

            for (int i = 1; i < parts.Length; i += 2)
            {
                if (parts.Length <= i + 1)
                    return;

                string pName = parts[i];
                string[] ipAndPort = parts[i + 1].Split(':');

                if (ipAndPort.Length < 2)
                    return;

                int port;
                bool success = int.TryParse(ipAndPort[1], out port);

                if (!success)
                    return;

                PlayerInfo pInfo = Players.Find(p => p.Name == pName);

                if (pInfo == null)
                    return;

                pInfo.Port = port;
            }

            StartGame();
        }

        protected override void StartGame()
        {
            AddNotice("Starting game..");

            FileHashCalculator fhc = new FileHashCalculator();
            fhc.CalculateHashes(GameModes);

            if (gameFilesHash != fhc.GetCompleteHash())
            {
                Logger.Log("Game files modified during client session!");
                channel.SendCTCPMessage(CHEAT_DETECTED_MESSAGE, QueuedMessageType.INSTANT_MESSAGE, 0);
                HandleCheatDetectedMessage(ProgramConstants.PLAYERNAME);
            }

            base.StartGame();
        }

        protected override void WriteSpawnIniAdditions(IniFile iniFile)
        {
            base.WriteSpawnIniAdditions(iniFile);

            iniFile.SetStringValue("Tunnel", "Ip", tunnel.Address);
            iniFile.SetIntValue("Tunnel", "Port", tunnel.Port);

            iniFile.SetIntValue("Settings", "GameID", UniqueGameID);
            iniFile.SetBooleanValue("Settings", "Host", IsHost);

            PlayerInfo localPlayer = Players.Find(p => p.Name == ProgramConstants.PLAYERNAME);

            if (localPlayer == null)
                return;

            iniFile.SetIntValue("Settings", "Port", localPlayer.Port);
        }

        protected override void SendChatMessage(string message)
        {
            channel.SendChatMessage(message, chatColor);
        }

        #region Notifications

        private void HandleNotification(string sender, Action handler)
        {
            if (sender != hostName)
                return;

            handler();
        }

        private void HandleIntNotification(string sender, int parameter, Action<int> handler)
        {
            if (sender != hostName)
                return;

            handler(parameter);
        }

        protected override void GetReadyNotification()
        {
            base.GetReadyNotification();

            WindowManager.FlashWindow();
            TopBar.SwitchToPrimary();

            if (IsHost)
                channel.SendCTCPMessage("GETREADY", QueuedMessageType.GAME_GET_READY_MESSAGE, 0);
        }

        protected override void AISpectatorsNotification()
        {
            base.AISpectatorsNotification();

            if (IsHost)
                channel.SendCTCPMessage("AISPECS", QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void InsufficientPlayersNotification()
        {
            base.InsufficientPlayersNotification();

            if (IsHost)
                channel.SendCTCPMessage("INSFSPLRS", QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void TooManyPlayersNotification()
        {
            base.TooManyPlayersNotification();

            if (IsHost)
                channel.SendCTCPMessage("TMPLRS", QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void SharedColorsNotification()
        {
            base.SharedColorsNotification();

            if (IsHost)
                channel.SendCTCPMessage("CLRS", QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void SharedStartingLocationNotification()
        {
            base.SharedStartingLocationNotification();

            if (IsHost)
                channel.SendCTCPMessage("SLOC", QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void LockGameNotification()
        {
            base.LockGameNotification();

            if (IsHost)
                channel.SendCTCPMessage("LCKGME", QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void NotVerifiedNotification(int playerIndex)
        {
            base.NotVerifiedNotification(playerIndex);

            if (IsHost)
                channel.SendCTCPMessage("NVRFY " + playerIndex, QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        protected override void StillInGameNotification(int playerIndex)
        {
            base.StillInGameNotification(playerIndex);

            if (IsHost)
                channel.SendCTCPMessage("INGM " + playerIndex, QueuedMessageType.GAME_NOTIFICATION_MESSAGE, 0);
        }

        private void ReturnNotification(string sender)
        {
            AddNotice(sender + " has returned from the game.");

            PlayerInfo pInfo = Players.Find(p => p.Name == sender);

            if (pInfo != null)
                pInfo.IsInGame = false;
        }

        private void TunnelPingNotification(string sender, int ping)
        {
            if (ping > -1)
            {
                AddNotice(sender + " - ping to tunnel server: " + ping + " ms");
            }
            else
                AddNotice(sender + " - unknown ping to tunnel server.");
        }

        private void FileHashNotification(string sender, string filesHash)
        {
            if (!IsHost)
                return;

            PlayerInfo pInfo = Players.Find(p => p.Name == sender);

            if (pInfo != null)
                pInfo.Verified = true;

            if (filesHash != gameFilesHash)
            {
                channel.SendCTCPMessage("MM " + sender, QueuedMessageType.GAME_CHEATER_MESSAGE, 10);
                CheaterNotification(ProgramConstants.PLAYERNAME, sender);
            }
        }

        private void CheaterNotification(string sender, string cheaterName)
        {
            if (sender != hostName)
                return;

            AddNotice("Player " + cheaterName + " has different files compared to the game host. Either " + 
                cheaterName + " or the game host could be cheating.", Color.Red);
        }

        #endregion

        protected override void HandleLockGameButtonClick()
        {
            if (!Locked)
            {
                AddNotice("You've locked the game room.");
                LockGame();
            }
            else
            {
                if (Players.Count < playerLimit)
                {
                    AddNotice("You've unlocked the game room.");
                    UnlockGame(false);
                }
                else
                    AddNotice(string.Format(
                        "Cannot unlock game; the player limit ({0}) has been reached.", playerLimit));
            }
        }

        protected override void LockGame()
        {
            connectionManager.SendCustomMessage(new QueuedMessage(
                string.Format("MODE {0} +i", channel.ChannelName), QueuedMessageType.INSTANT_MESSAGE, -1));

            Locked = true;
            btnLockGame.Text = "Unlock Game";
            AccelerateGameBroadcasting();
        }

        protected override void UnlockGame(bool announce)
        {
            connectionManager.SendCustomMessage(new QueuedMessage(
                string.Format("MODE {0} -i", channel.ChannelName), QueuedMessageType.SYSTEM_MESSAGE, 10));

            Locked = false;
            if (announce)
                AddNotice("The game room has been unlocked.");
            btnLockGame.Text = "Lock Game";
            AccelerateGameBroadcasting();
        }

        protected override void KickPlayer(int playerIndex)
        {
            if (playerIndex >= Players.Count)
                return;

            var pInfo = Players[playerIndex];

            AddNotice("Kicking " + pInfo.Name + " from the game...");
            channel.SendKickMessage(pInfo.Name, 8);
        }

        protected override void BanPlayer(int playerIndex)
        {
            if (playerIndex >= Players.Count)
                return;

            var pInfo = Players[playerIndex];

            var user = connectionManager.UserList.Find(u => u.Name == pInfo.Name);

            if (user != null)
            {
                AddNotice("Banning and kicking " + pInfo.Name + " from the game...");
                channel.SendBanMessage(user.Hostname, 8);
                channel.SendKickMessage(user.Name, 8);
            }
        }

        protected override void BroadcastFrameSendRate(int value)
        {
            channel.SendCTCPMessage(FRAME_SEND_RATE_MESSAGE + " " + FrameSendRate, QueuedMessageType.UNDEFINED, 12);
        }

        private void SetFrameSendRateForNonHostPlayer(string sender, int frameSendRate)
        {
            if (sender != hostName)
                return;

            FrameSendRate = frameSendRate;
            AddNotice("The game host has changed FrameSendRate (order lag) to " + frameSendRate);
            ClearReadyStatuses();
        }

        private void HandleCheatDetectedMessage(string sender)
        {
            AddNotice(sender + " has modified game files during the client session. They are likely attempting to cheat!", Color.Red);
        }

        #region CnCNet map sharing

        private void MapSharer_MapDownloadFailed(object sender, SHA1EventArgs e)
        {
            WindowManager.AddCallback(new Action<SHA1EventArgs>(MapSharer_HandleMapDownloadFailed), e);
        }

        private void MapSharer_HandleMapDownloadFailed(SHA1EventArgs e)
        {
            // If the host has already uploaded the map, we shouldn't request them to re-upload it
            if (hostUploadedMaps.Contains(e.SHA1))
            {
                AddNotice("Download of the custom map failed. The host needs to change the map or you will be unable to participate in this match.");

                channel.SendCTCPMessage(MAP_SHARING_FAIL_MESSAGE + " " + e.SHA1, QueuedMessageType.SYSTEM_MESSAGE, 9);
                return;
            }

            AddNotice("Requesting the game host to upload the map to the CnCNet map database.");

            channel.SendCTCPMessage(MAP_SHARING_UPLOAD_REQUEST + " " + e.SHA1, QueuedMessageType.SYSTEM_MESSAGE, 9);
        }

        private void MapSharer_MapDownloadComplete(object sender, SHA1EventArgs e)
        {
            WindowManager.AddCallback(new Action<SHA1EventArgs>(MapSharer_HandleMapDownloadComplete), e);
        }

        private void MapSharer_HandleMapDownloadComplete(SHA1EventArgs e)
        {
            string mapPath = "Maps\\Custom\\" + e.SHA1;
            Map map = new Map(mapPath, false);

            if (map.SetInfoFromMap(ProgramConstants.GamePath + mapPath + ".map"))
            {
                Logger.Log("Map " + e.SHA1 + " downloaded succesfully.");
                AddNotice("Map succesfully transferred.");

                foreach (string gameMode in map.GameModes)
                {
                    GameMode gm = GameModes.Find(g => g.UIName == gameMode);

                    if (gm == null)
                    {
                        gm = new GameMode();
                        gm.Name = gameMode;
                        gm.Initialize();
                        GameModes.Add(gm);
                        ddGameMode.AddItem(gm.UIName);
                    }

                    gm.Maps.Add(map);
                }

                if (lastMapSHA1 == e.SHA1)
                {
                    Map = map;
                    GameMode = GameModes.Find(gm => gm.UIName == lastGameMode);
                    ChangeMap(GameMode, Map);
                }
            }
            else
            {
                Logger.Log("Loading map " + e.SHA1 + " failed!");
                AddNotice("Transfer of the custom map failed. The host needs to change the map or you will be unable to participate in this match.");

                channel.SendCTCPMessage(MAP_SHARING_FAIL_MESSAGE + " " + e.SHA1, QueuedMessageType.SYSTEM_MESSAGE, 9);
            }
        }

        private void MapSharer_MapUploadFailed(object sender, MapEventArgs e)
        {
            WindowManager.AddCallback(new Action<MapEventArgs>(MapSharer_HandleMapUploadFailed), e);
        }

        private void MapSharer_HandleMapUploadFailed(MapEventArgs e)
        {
            Map map = e.Map;

            hostUploadedMaps.Add(map.SHA1);

            AddNotice("Uploading map " + map.Name + " to the CnCNet map database failed.");
            if (map == Map)
            {
                AddNotice("You need to change the map or some players won't be able to participate in this match.");
                channel.SendCTCPMessage(MAP_SHARING_FAIL_MESSAGE + " " + map.SHA1, QueuedMessageType.SYSTEM_MESSAGE, 9);
            }
        }

        private void MapSharer_MapUploadComplete(object sender, MapEventArgs e)
        {
            WindowManager.AddCallback(new Action<MapEventArgs>(MapSharer_HandleMapUploadComplete), e);
        }

        private void MapSharer_HandleMapUploadComplete(MapEventArgs e)
        {
            hostUploadedMaps.Add(e.Map.SHA1);

            AddNotice("Uploading map " + e.Map.Name + " to the CnCNet map database complete.");
            if (e.Map == Map)
            {
                channel.SendCTCPMessage(MAP_SHARING_DOWNLOAD_REQUEST + " " + Map.SHA1, QueuedMessageType.SYSTEM_MESSAGE, 9);
            }
        }

        /// <summary>
        /// Handles a map upload request sent by a player.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="mapSHA1">The SHA1 of the requested map.</param>
        private void HandleMapUploadRequest(string sender, string mapSHA1)
        {
            if (hostUploadedMaps.Contains(mapSHA1))
            {
                Logger.Log("HandleMapUploadRequest: Map " + mapSHA1 + " is already uploaded!");
                return;
            }

            Map map = null;

            foreach (GameMode gm in GameModes)
            {
                map = gm.Maps.Find(m => m.SHA1 == mapSHA1);

                if (map != null)
                    break;
            }

            if (map == null)
            {
                Logger.Log("Unknown map upload request from " + sender + ": " + mapSHA1);
                return;
            }

            if (map.Official)
            {
                Logger.Log("HandleMapUploadRequest: Map is official, so skip request");

                AddNotice(string.Format("{0} doesn't have the map '{1}' on their local installation. " + 
                    "The map needs to be changed or {0} is unable to participate in the match.",
                    sender, map.Name));

                return;
            }

            if (!IsHost)
                return;

            AddNotice(string.Format("{0} doesn't have the map '{1}' on their local installation. " +
                "Attempting to upload the map to the CnCNet map database.",
                sender, map.Name));

            MapSharer.UploadMap(map, localGame);
        }

        /// <summary>
        /// Handles a map transfer failure message sent by either the player or the game host.
        /// </summary>
        private void HandleMapTransferFailMessage(string sender, string sha1)
        {
            if (sender == hostName)
            {
                AddNotice("The game host failed to upload the map to the CnCNet map database.");

                hostUploadedMaps.Add(sha1);

                if (lastMapSHA1 == sha1 && Map == null)
                {
                    AddNotice("The game host needs to change the map or you won't be able to participate in this match.");
                }

                return;
            }

            if (lastMapSHA1 == sha1)
            {
                if (!IsHost)
                {
                    AddNotice(sender + " has failed to download the map from the CnCNet map database. " +
                        "The host needs to change the map or " + sender + " won't be able to participate in this match.");
                }
                else
                {
                    AddNotice(sender + " has failed to download the map from the CnCNet map database. " +
                        "You need to change the map or " + sender + " won't be able to participate in this match.");
                }
            }
        }

        private void HandleMapDownloadRequest(string sender, string sha1)
        {
            if (sender != hostName)
                return;

            hostUploadedMaps.Add(sha1);

            if (lastMapSHA1 == sha1 && Map == null)
            {
                Logger.Log("The game host has uploaded the map into the database. Re-attempting download...");
                MapSharer.DownloadMap(sha1, localGame);
            }
        }

        private void HandleMapSharingBlockedMessage(string sender)
        {
            AddNotice("The selected map doesn't exist on " + sender + "'s installation, and they " +
                "have map sharing disabled in settings. The game host needs to change to a non-custom map or " +
                "they will be unable to participate in this match.");
        }

        #endregion

        #region Game broadcasting logic

        /// <summary>
        /// Lowers the time until the next game broadcasting message.
        /// </summary>
        private void AccelerateGameBroadcasting()
        {
            gameBroadcastTimer.Accelerate(TimeSpan.FromSeconds(GAME_BROADCAST_ACCELERATION));
        }

        private void BroadcastGame()
        {
            Channel broadcastChannel = connectionManager.GetChannel("#cncnet-" + localGame.ToLower() + "-games");

            if (broadcastChannel == null)
                return;

            if (GameMode == null || Map == null)
                return;

            StringBuilder sb = new StringBuilder("GAME ");
            sb.Append(ProgramConstants.CNCNET_PROTOCOL_REVISION);
            sb.Append(";");
            sb.Append(ProgramConstants.GAME_VERSION);
            sb.Append(";");
            sb.Append(playerLimit);
            sb.Append(";");
            sb.Append(channel.ChannelName);
            sb.Append(";");
            sb.Append(channel.UIName);
            sb.Append(";");
            if (Locked)
                sb.Append("1");
            else
                sb.Append("0");
            sb.Append(Convert.ToInt32(isCustomPassword));
            sb.Append(Convert.ToInt32(closed));
            sb.Append("0"); // IsLoadedGame
            sb.Append("0"); // IsLadder
            sb.Append(";");
            foreach (PlayerInfo pInfo in Players)
            {
                sb.Append(pInfo.Name);
                sb.Append(",");
            }

            sb.Remove(sb.Length - 1, 1);
            sb.Append(";");
            sb.Append(Map.Name);
            sb.Append(";");
            sb.Append(GameMode.UIName);
            sb.Append(";");
            sb.Append(tunnel.Address);
            sb.Append(";");
            sb.Append(0); // LoadedGameId

            broadcastChannel.SendCTCPMessage(sb.ToString(), QueuedMessageType.SYSTEM_MESSAGE, 20);
        }

        #endregion

        public override string GetSwitchName()
        {
            return "Game Lobby";
        }
    }
}

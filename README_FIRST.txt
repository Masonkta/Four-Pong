FOUR-WAY PONG — UNITY 6 / EOS ROOM-CODE BUILD
==============================================

OPENING THE PROJECT
-------------------
1. Extract this ZIP.
2. Unity Hub -> Add -> Add project from disk.
3. Select the FourWayPong_EOS_RoomCodes folder (the folder containing Assets, Packages, and ProjectSettings).
4. Open with Unity 6.0 LTS. The project version file is 6000.0.23f1.
5. Let Package Manager finish importing Netcode for GameObjects and EOS Plugin for Unity.
6. Complete EOS_ROOM_CODES_SETUP.txt once for your Epic Developer Portal product.
7. The editor setup script creates Assets/FourWayPong.unity and adds it to the build scenes.

If you need to regenerate it:
  Four-Way Pong -> Rebuild Main Scene

MAIN MENU
---------
LOCAL
- Exactly 2 players on one keyboard.
- Player 1 controls Left.
- Player 2 controls Right.
- Default controls: P1 W/S, P2 Up/Down arrows.

MULTIPLAYER
- Host -> Host Game -> gets a 6-character room code.
- Join -> enter that room code.
- No IP address or port is shown to the player.
- No Playit.
- No router port forwarding.
- No Unity Relay/Lobby/Authentication.
- EOS Lobby supplies the join code and EOS P2P carries Netcode traffic.
- Up to four players.
- 2 players: Left + Right.
- 3 players: Left + Right + Player 3 chooses Top or Bottom.
- 4 players: all four paddles.
- Players can cycle team IDs for 1v1, FFA, 2v2, 3v1, 2v1v1, etc.

SETTINGS
- Master volume and mute.
- Separate Player 1 / Player 2 keybind columns.
- Local uses both columns.
- Online uses Player 1's bindings for the local network paddle.
- Settings persist via PlayerPrefs.

GAMEPLAY TUNING
---------------
Edit:
  Assets/Scripts/FourWayPong/PongConfig.cs

Current important values:
  PaddleSpeed = 8.5
  BallStartSpeed = 7.25
  BallAccelerationPerSecond = 0.34
  ScoreToWin = 7
  CornerPaddleGap = 0.18

Ball speed has NO maximum. It accelerates continuously until a score, then the next serve resets to BallStartSpeed.
Adaptive movement substeps reduce tunneling as the uncapped ball gets very fast.

ACTIVE PADDLES AND CORNERS
--------------------------
- Missing Player 3/4 paddles are not drawn and do not collide.
- An unused top/bottom side behaves as a bounce wall.
- Corner blocking only exists when both perpendicular paddles actually exist.
- This works in both 3-player and 4-player matches.

NETWORK PREFAB FIX
------------------
- PongMatch is now generated as Assets/Generated/PongMatch.prefab.
- It is registered with Netcode before Host/Join starts.
- The host spawns PongMatch only after NetworkManager.StartHost succeeds.
- PongMatch is no longer placed directly in the scene, avoiding the NGO error about an unregistered/pre-instantiated NetworkObject.
- Four-Way Pong -> Rebuild Main Scene now rebuilds the corrected prefab-based setup instead of recreating the old broken scene object.

BUILDING WINDOWS
----------------
1. File -> Build Profiles.
2. Select Windows.
3. Ensure Assets/FourWayPong.unity is included.
4. Build.
5. Give friends the entire generated build folder (usually zipped), not just the .exe.

NETWORK PACKAGES
----------------
- com.unity.netcode.gameobjects 2.7.0
- com.playeveryware.eos from EOS-Contrib/eos_plugin_for_unity_upm v6.2.0

Read EOS_ROOM_CODES_SETUP.txt before your first online test.

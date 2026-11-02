using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using UnityEngine;

namespace FourWayPong
{
    public sealed class Battle99Game : MonoBehaviour
    {
        private const int Columns = 10;
        private const int Rows = 16;
        private const float BoardHalfWidth = 5f;
        private const float BoardHeight = 12f;
        private const float PaddleY = 0.72f;
        private const float PaddleBaseLength = 2.15f;
        private const float PaddleHeight = 0.26f;
        private const float BallRadius = 0.15f;
        private const float BallStartSpeed = 7.5f;
        private const float BallMaxPresentationSpeed = 13.5f;
        private const float PaddleSpeed = 9.5f;
        private const float BrickWidth = 0.82f;
        private const float BrickHeight = 0.42f;
        private const float BrickGapX = 0.10f;
        private const float BrickGapY = 0.09f;
        private const float BrickTopY = 11.35f;
        private const float PickupFallSpeed = 2.1f;
        private const float PickupChance = 0.15f;
        private const int MaxBalls = 4;
        private const int MaxPickups = 16;
        private const float FixedStep = 1f / 120f;
        private const float SnapshotRate = 10f;
        private const float ComboWindow = 2.25f;
        private const float GarbageWarning = 1.6f;
        private const float MatchCountdown = 3f;

        private struct Ball
        {
            public bool Active;
            public Vector2 Position;
            public Vector2 Velocity;
        }

        private struct Pickup
        {
            public bool Active;
            public int Type; // 0 multiball, 1 extend. No shrinking power-up in Battle 99.
            public Vector2 Position;
        }

        private readonly bool[,] _bricks = new bool[Columns, Rows];
        private readonly Ball[] _balls = new Ball[MaxBalls];
        private readonly Pickup[] _pickups = new Pickup[MaxPickups];
        private readonly List<Battle99Member> _roster = new List<Battle99Member>();
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>();

        private Battle99Service _service;
        private IBattle99NetworkLink _peer;
        private ProductUserId _localUser;
        private float _paddleX;
        private float _paddleScale = 1f;
        private float _accumulator;
        private float _matchTime;
        private float _countdown;
        private float _respawnTimer;
        private bool _waitingForRespawn;
        private float _pressureTimer;
        private int _pendingGarbage;
        private float _garbageTimer;
        private int _counterProgress;
        private int _comboCount;
        private int _lastComboTier;
        private float _comboTimer;
        private uint _snapshotSequence;
        private float _snapshotTimer;
        private string _popupText = string.Empty;
        private float _popupStarted;
        private float _popupDuration;
        private ProductUserId _spectatedUser;
        private int _initialPlayerCount;

        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _centerStyle;
        private GUIStyle _countdownStyle;
        private Texture2D _backgroundTex;
        private Texture2D _panelTex;
        private Texture2D _brickTex;
        private Texture2D _garbageTex;
        private Texture2D _paddleTex;
        private Texture2D _ballTex;
        private Texture2D _multiTex;
        private Texture2D _extendTex;
        private Texture2D _dangerTex;

        public bool Active { get; private set; }
        public bool Eliminated { get; private set; }
        public bool Winner { get; private set; }
        public bool Spectating { get; private set; }
        public int Placement { get; private set; }
        public float PingMs => _peer != null ? _peer.PingMs : -1f;
        public int PendingGarbage => _pendingGarbage;

        public void StartBattle(Battle99Service service)
        {
            StopBattle();
            if (service == null || service.LocalUserId == null || !service.LocalUserId.IsValid())
                return;

            _service = service;
            _localUser = service.LocalUserId;
            _roster.Clear();
            _names.Clear();
            for (int i = 0; i < service.MatchRoster.Count; i++)
            {
                Battle99Member source = service.MatchRoster[i];
                Battle99Member member = new Battle99Member { UserId = source.UserId, Name = source.Name, Ready = true };
                _roster.Add(member);
                if (member.UserId != null && member.UserId.IsValid())
                    _names[member.UserId.ToString()] = string.IsNullOrWhiteSpace(member.Name) ? "Player" : member.Name;
            }

            _initialPlayerCount = _roster.Count;
            _peer = service.UsesDedicatedServer
                ? (IBattle99NetworkLink)service.DedicatedClient
                : GetComponent<Battle99PeerService>();

            if (_peer == null && !service.UsesDedicatedServer)
                _peer = gameObject.AddComponent<Battle99PeerService>();

            if (_peer == null)
                return;

            _peer.AttackReceived -= OnAttackReceived;
            _peer.AttackReceived += OnAttackReceived;
            _peer.LocalPlacementReceived -= OnServerPlacement;
            _peer.LocalPlacementReceived += OnServerPlacement;
            _peer.StartPeer(_localUser, service.DedicatedServerUserId ?? service.OwnerUserId, _roster);

            ClearBoard();
            for (int row = 0; row < 5; row++)
                GenerateRow(row, false);

            Array.Clear(_balls, 0, _balls.Length);
            Array.Clear(_pickups, 0, _pickups.Length);
            _paddleX = 0f;
            _paddleScale = 1f;
            _accumulator = 0f;
            _matchTime = 0f;
            _countdown = MatchCountdown;
            _waitingForRespawn = false;
            _respawnTimer = 0f;
            _pressureTimer = 13f;
            _pendingGarbage = 0;
            _garbageTimer = 0f;
            _counterProgress = 0;
            _comboCount = 0;
            _lastComboTier = 0;
            _comboTimer = 0f;
            _snapshotSequence = 0;
            _snapshotTimer = 0f;
            _popupText = string.Empty;
            _spectatedUser = null;
            Eliminated = false;
            Winner = false;
            Spectating = false;
            Placement = 0;
            Active = true;
        }

        public void StopBattle()
        {
            Active = false;
            Eliminated = false;
            Winner = false;
            Spectating = false;
            _spectatedUser = null;
            if (_peer != null)
            {
                _peer.AttackReceived -= OnAttackReceived;
                _peer.LocalPlacementReceived -= OnServerPlacement;
                _peer.StopPeer();
            }
        }

        private void Update()
        {
            if (!Active)
                return;

            if (_service != null)
            {
                _peer.SetCoordinator(_service.DedicatedServerUserId ?? _service.OwnerUserId);
                _peer.SetRoster(_service.MatchStarted ? _service.MatchRoster : _service.Members);
            }

            if (!Eliminated && !Winner)
            {
                CapturePaddleInput(Time.unscaledDeltaTime);
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                _accumulator = Mathf.Min(_accumulator + dt, FixedStep * 8f);
                int steps = 0;
                while (_accumulator >= FixedStep && steps < 8)
                {
                    Tick(FixedStep);
                    _accumulator -= FixedStep;
                    steps++;
                }
            }

            _snapshotTimer += Time.unscaledDeltaTime;
            if (_snapshotTimer >= 1f / SnapshotRate)
            {
                _snapshotTimer = 0f;
                BroadcastSnapshot();
            }

            if (!Eliminated && !Winner && _matchTime > 4f && _peer.CountAliveRemotePlayers() == 0 && _initialPlayerCount >= 2)
            {
                Winner = true;
                Placement = 1;
                ShowPopup("LAST PLAYER STANDING", 2f);
                BroadcastSnapshot();
            }
        }

        private void CapturePaddleInput(float dt)
        {
            float half = PaddleLength * 0.5f;
            float limit = BoardHalfWidth - half - 0.08f;

            if (PongSettings.Player1UsesMouse)
            {
                float normalized = Screen.width > 1f ? Mathf.Clamp01(Input.mousePosition.x / Screen.width) : 0.5f;
                float target = Mathf.Lerp(-limit, limit, normalized);
                _paddleX = Mathf.MoveTowards(_paddleX, target, PaddleSpeed * 1.7f * dt);
            }
            else
            {
                float input = 0f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) input -= 1f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) input += 1f;
                _paddleX = Mathf.Clamp(_paddleX + input * PaddleSpeed * dt, -limit, limit);
            }
        }

        private float PaddleLength => PaddleBaseLength * _paddleScale;

        private void Tick(float dt)
        {
            _matchTime += dt;

            if (_countdown > 0f)
            {
                _countdown -= dt;
                if (_countdown <= 0f)
                    SpawnServeBall();
                return;
            }

            if (_comboTimer > 0f)
            {
                _comboTimer -= dt;
                if (_comboTimer <= 0f)
                {
                    _comboCount = 0;
                    _lastComboTier = 0;
                }
            }

            if (_pendingGarbage > 0)
            {
                _garbageTimer -= dt;
                if (_garbageTimer <= 0f)
                {
                    int rows = _pendingGarbage;
                    _pendingGarbage = 0;
                    _counterProgress = 0;
                    for (int i = 0; i < rows && !Eliminated; i++)
                        PushNewRow(true);
                }
            }

            _pressureTimer -= dt;
            if (_pressureTimer <= 0f)
            {
                PushNewRow(false);
                float pressureInterval = Mathf.Max(5f, 13f - Mathf.Floor(_matchTime / 45f) * 1.1f);
                _pressureTimer = pressureInterval;
            }

            if (_waitingForRespawn)
            {
                _respawnTimer -= dt;
                if (_respawnTimer <= 0f)
                {
                    _waitingForRespawn = false;
                    SpawnServeBall();
                }
            }

            TickPickups(dt);
            TickBalls(dt);

            if (!Eliminated && !_waitingForRespawn && CountActiveBalls() == 0 && _countdown <= 0f)
            {
                PushNewRow(false);
                if (!Eliminated)
                {
                    _waitingForRespawn = true;
                    _respawnTimer = 0.8f;
                    ShowPopup("BALL LOST", 1f);
                }
            }

            if (!Eliminated && CountBricks() == 0)
            {
                GenerateRow(0, false);
                GenerateRow(1, false);
            }
        }

        private void TickBalls(float dt)
        {
            float speed = Mathf.Min(BallMaxPresentationSpeed, BallStartSpeed + _matchTime * 0.025f);
            for (int i = 0; i < _balls.Length; i++)
            {
                if (!_balls[i].Active)
                    continue;

                Ball ball = _balls[i];
                if (ball.Velocity.sqrMagnitude < 0.01f)
                    ball.Velocity = new Vector2(0.4f, 1f).normalized * speed;
                else
                    ball.Velocity = ball.Velocity.normalized * speed;

                int substeps = Mathf.Clamp(Mathf.CeilToInt(speed * dt / 0.10f), 1, 5);
                float step = dt / substeps;
                for (int s = 0; s < substeps && ball.Active; s++)
                    TickBallStep(ref ball, step);

                _balls[i] = ball;
            }
        }

        private void TickBallStep(ref Ball ball, float dt)
        {
            Vector2 pos = ball.Position + ball.Velocity * dt;
            Vector2 vel = ball.Velocity;

            if (pos.x - BallRadius < -BoardHalfWidth)
            {
                pos.x = -BoardHalfWidth + BallRadius;
                vel.x = Mathf.Abs(vel.x);
            }
            else if (pos.x + BallRadius > BoardHalfWidth)
            {
                pos.x = BoardHalfWidth - BallRadius;
                vel.x = -Mathf.Abs(vel.x);
            }

            if (pos.y + BallRadius > BoardHeight)
            {
                pos.y = BoardHeight - BallRadius;
                vel.y = -Mathf.Abs(vel.y);
            }

            float halfPaddle = PaddleLength * 0.5f;
            if (vel.y < 0f &&
                pos.y - BallRadius <= PaddleY + PaddleHeight * 0.5f &&
                pos.y > PaddleY - 0.45f &&
                Mathf.Abs(pos.x - _paddleX) <= halfPaddle + BallRadius)
            {
                float offset = Mathf.Clamp((pos.x - _paddleX) / Mathf.Max(0.1f, halfPaddle), -1f, 1f);
                pos.y = PaddleY + PaddleHeight * 0.5f + BallRadius;
                vel = new Vector2(offset * 0.85f, 1f).normalized * vel.magnitude;
            }

            if (pos.y < -0.3f)
            {
                ball.Active = false;
                ball.Position = pos;
                ball.Velocity = vel;
                return;
            }

            if (TryHitBrick(pos, out int col, out int row))
            {
                BreakBrick(col, row);
                vel.y = -vel.y;
                pos += vel.normalized * 0.03f;
            }

            ball.Position = pos;
            ball.Velocity = vel;
        }

        private bool TryHitBrick(Vector2 pos, out int hitCol, out int hitRow)
        {
            hitCol = -1;
            hitRow = -1;
            float stepX = BrickWidth + BrickGapX;
            float stepY = BrickHeight + BrickGapY;
            float left = -stepX * Columns * 0.5f + BrickGapX * 0.5f;
            int col = Mathf.FloorToInt((pos.x - left) / stepX);
            int row = Mathf.FloorToInt((BrickTopY + BrickHeight * 0.5f - pos.y) / stepY);

            for (int r = row - 1; r <= row + 1; r++)
            {
                if (r < 0 || r >= Rows)
                    continue;
                for (int c = col - 1; c <= col + 1; c++)
                {
                    if (c < 0 || c >= Columns || !_bricks[c, r])
                        continue;

                    Vector2 center = BrickCenter(c, r);
                    if (Mathf.Abs(pos.x - center.x) <= BrickWidth * 0.5f + BallRadius &&
                        Mathf.Abs(pos.y - center.y) <= BrickHeight * 0.5f + BallRadius)
                    {
                        hitCol = c;
                        hitRow = r;
                        return true;
                    }
                }
            }
            return false;
        }

        private void BreakBrick(int col, int row)
        {
            if (!_bricks[col, row])
                return;

            _bricks[col, row] = false;
            Vector2 center = BrickCenter(col, row);
            if (UnityEngine.Random.value < PickupChance)
                SpawnPickup(center);

            if (_comboTimer > 0f)
                _comboCount++;
            else
                _comboCount = 1;
            _comboTimer = ComboWindow;

            if (_pendingGarbage > 0)
            {
                _counterProgress++;
                if (_counterProgress >= 5)
                {
                    _counterProgress = 0;
                    _pendingGarbage = Mathf.Max(0, _pendingGarbage - 1);
                    ShowPopup("COUNTER", 0.9f);
                    if (_pendingGarbage == 0)
                        _garbageTimer = 0f;
                }
            }

            int tier = _comboCount / 6;
            if (tier > _lastComboTier)
            {
                _lastComboTier = tier;
                int rowsToSend = tier >= 3 ? 2 : 1;
                SendAttack(rowsToSend);
            }
        }

        private void SendAttack(int rows)
        {
            List<ProductUserId> targets = _peer.GetAliveRemoteUsers();
            if (targets.Count == 0)
                return;

            ProductUserId target = targets[UnityEngine.Random.Range(0, targets.Count)];
            _peer.SendAttack(target, rows);
            ShowPopup(rows > 1 ? "ATTACK +2" : "ATTACK +1", 0.95f);
        }

        private void OnAttackReceived(ProductUserId sender, int rows)
        {
            if (!Active || Eliminated || Winner)
                return;

            _pendingGarbage = Mathf.Clamp(_pendingGarbage + rows, 0, 8);
            _garbageTimer = GarbageWarning;
            ShowPopup("INCOMING " + _pendingGarbage, 1.1f);
        }

        private void TickPickups(float dt)
        {
            float halfPaddle = PaddleLength * 0.5f;
            for (int i = 0; i < _pickups.Length; i++)
            {
                if (!_pickups[i].Active)
                    continue;

                Pickup pickup = _pickups[i];
                pickup.Position.y -= PickupFallSpeed * dt;
                if (pickup.Position.y <= PaddleY + 0.28f && pickup.Position.y >= PaddleY - 0.45f &&
                    Mathf.Abs(pickup.Position.x - _paddleX) <= halfPaddle + 0.22f)
                {
                    ApplyPickup(pickup.Type);
                    pickup.Active = false;
                }
                else if (pickup.Position.y < -0.5f)
                {
                    pickup.Active = false;
                }
                _pickups[i] = pickup;
            }
        }

        private void SpawnPickup(Vector2 position)
        {
            for (int i = 0; i < _pickups.Length; i++)
            {
                if (_pickups[i].Active)
                    continue;

                _pickups[i] = new Pickup
                {
                    Active = true,
                    Type = UnityEngine.Random.value < 0.52f ? 0 : 1,
                    Position = position
                };
                return;
            }
        }

        private void ApplyPickup(int type)
        {
            if (type == 0)
            {
                AddMultiball();
                ShowPopup("MULTIBALL", 1.1f);
            }
            else
            {
                _paddleScale = Mathf.Min(1.85f, _paddleScale + 0.22f);
                ShowPopup("PADDLE EXTEND", 1.1f);
            }
        }

        private void AddMultiball()
        {
            int source = -1;
            for (int i = 0; i < _balls.Length; i++)
            {
                if (_balls[i].Active)
                {
                    source = i;
                    break;
                }
            }
            if (source < 0)
                return;

            for (int i = 0; i < _balls.Length; i++)
            {
                if (_balls[i].Active)
                    continue;

                Ball src = _balls[source];
                Vector2 v = src.Velocity;
                float angle = UnityEngine.Random.value < 0.5f ? -24f : 24f;
                float radians = angle * Mathf.Deg2Rad;
                Vector2 rotated = new Vector2(v.x * Mathf.Cos(radians) - v.y * Mathf.Sin(radians), v.x * Mathf.Sin(radians) + v.y * Mathf.Cos(radians));
                _balls[i] = new Ball { Active = true, Position = src.Position + Vector2.right * 0.12f, Velocity = rotated };
                return;
            }
        }

        private void PushNewRow(bool garbage)
        {
            if (Eliminated)
                return;

            for (int c = 0; c < Columns; c++)
            {
                if (_bricks[c, Rows - 1])
                {
                    Eliminate();
                    return;
                }
            }

            for (int row = Rows - 1; row >= 1; row--)
                for (int col = 0; col < Columns; col++)
                    _bricks[col, row] = _bricks[col, row - 1];

            GenerateRow(0, garbage);

            for (int c = 0; c < Columns; c++)
            {
                if (_bricks[c, Rows - 1])
                {
                    Eliminate();
                    return;
                }
            }
        }

        private void GenerateRow(int row, bool garbage)
        {
            if (row < 0 || row >= Rows)
                return;

            int holes = garbage ? 2 : UnityEngine.Random.Range(2, 5);
            bool[] hole = new bool[Columns];
            for (int i = 0; i < holes; i++)
                hole[UnityEngine.Random.Range(0, Columns)] = true;

            for (int col = 0; col < Columns; col++)
            {
                if (garbage)
                    _bricks[col, row] = !hole[col];
                else
                    _bricks[col, row] = !hole[col] && UnityEngine.Random.value < 0.88f;
            }
        }

        private void ClearBoard()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                    _bricks[c, r] = false;
        }

        private int CountBricks()
        {
            int count = 0;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                    if (_bricks[c, r]) count++;
            return count;
        }

        private int CountActiveBalls()
        {
            int count = 0;
            for (int i = 0; i < _balls.Length; i++)
                if (_balls[i].Active) count++;
            return count;
        }

        private void SpawnServeBall()
        {
            if (Eliminated || Winner)
                return;

            for (int i = 0; i < _balls.Length; i++)
                _balls[i].Active = false;

            float horizontal = UnityEngine.Random.Range(-0.55f, 0.55f);
            _balls[0] = new Ball
            {
                Active = true,
                Position = new Vector2(_paddleX, PaddleY + 0.55f),
                Velocity = new Vector2(horizontal, 1f).normalized * BallStartSpeed
            };
        }

        private void Eliminate()
        {
            if (Eliminated || Winner)
                return;

            Eliminated = true;
            Placement = Mathf.Clamp(_peer.CountAliveRemotePlayers() + 1, 2, Mathf.Max(2, _initialPlayerCount));
            for (int i = 0; i < _balls.Length; i++) _balls[i].Active = false;
            for (int i = 0; i < _pickups.Length; i++) _pickups[i].Active = false;
            ShowPopup("OUT", 1.4f);
            BroadcastSnapshot();
        }

        private void OnServerPlacement(int placement)
        {
            Placement = Mathf.Max(1, placement);
            if (Placement == 1 && !Eliminated)
            {
                Winner = true;
                ShowPopup("LAST PLAYER STANDING", 2f);
            }
        }

        public void BeginSpectate()
        {
            if (!Eliminated)
                return;
            Spectating = true;
            SelectNextSpectated(1);
        }

        public void StopSpectate()
        {
            Spectating = false;
            _spectatedUser = null;
        }

        public void SelectNextSpectated(int direction)
        {
            if (_roster.Count <= 1)
                return;

            // Spectating can cycle through every other member, including players who are
            // already out. Eliminated boards remain as frozen OUT boards.
            List<ProductUserId> candidates = new List<ProductUserId>();
            for (int i = 0; i < _roster.Count; i++)
            {
                ProductUserId user = _roster[i].UserId;
                if (user != null && user.IsValid() && !Equals(user, _localUser))
                    candidates.Add(user);
            }

            if (candidates.Count == 0)
                return;

            int current = -1;
            if (_spectatedUser != null)
            {
                string wanted = _spectatedUser.ToString();
                for (int i = 0; i < candidates.Count; i++)
                    if (candidates[i].ToString() == wanted) current = i;
            }

            int next = current < 0 ? 0 : (current + (direction >= 0 ? 1 : -1) + candidates.Count) % candidates.Count;
            _spectatedUser = candidates[next];
        }

        public string SpectatedName
        {
            get
            {
                if (_spectatedUser == null)
                    return string.Empty;
                return GetName(_spectatedUser);
            }
        }

        private void BroadcastSnapshot()
        {
            if (_peer == null)
                return;

            Battle99NetSnapshot snapshot = new Battle99NetSnapshot
            {
                Sequence = ++_snapshotSequence,
                Alive = !Eliminated,
                PaddleNormalized = Mathf.InverseLerp(-BoardHalfWidth, BoardHalfWidth, _paddleX),
                PaddleScale = _paddleScale,
                PendingGarbage = (byte)Mathf.Clamp(_pendingGarbage, 0, 255)
            };

            PackBricks(out snapshot.BrickLow, out snapshot.BrickMid, out snapshot.BrickHigh);
            byte mask = 0;
            if (_balls[0].Active) { mask |= 1; snapshot.Ball0 = NormalizeBoardPosition(_balls[0].Position); }
            if (_balls[1].Active) { mask |= 2; snapshot.Ball1 = NormalizeBoardPosition(_balls[1].Position); }
            if (_balls[2].Active) { mask |= 4; snapshot.Ball2 = NormalizeBoardPosition(_balls[2].Position); }
            if (_balls[3].Active) { mask |= 8; snapshot.Ball3 = NormalizeBoardPosition(_balls[3].Position); }
            snapshot.BallMask = mask;
            _peer.BroadcastSnapshot(snapshot);
        }

        private void PackBricks(out ulong low, out ulong mid, out uint high)
        {
            low = 0UL;
            mid = 0UL;
            high = 0U;
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Columns; col++)
                {
                    if (!_bricks[col, row])
                        continue;
                    int bit = row * Columns + col;
                    if (bit < 64) low |= 1UL << bit;
                    else if (bit < 128) mid |= 1UL << (bit - 64);
                    else high |= 1U << (bit - 128);
                }
            }
        }

        private static bool SnapshotBrickAlive(Battle99NetSnapshot snapshot, int col, int row)
        {
            int bit = row * Columns + col;
            if (bit < 64) return (snapshot.BrickLow & (1UL << bit)) != 0;
            if (bit < 128) return (snapshot.BrickMid & (1UL << (bit - 64))) != 0;
            return (snapshot.BrickHigh & (1U << (bit - 128))) != 0;
        }

        private static Vector2 NormalizeBoardPosition(Vector2 position)
        {
            return new Vector2(
                Mathf.InverseLerp(-BoardHalfWidth, BoardHalfWidth, position.x),
                Mathf.InverseLerp(0f, BoardHeight, position.y));
        }

        private static Vector2 BrickCenter(int col, int row)
        {
            float stepX = BrickWidth + BrickGapX;
            float stepY = BrickHeight + BrickGapY;
            float totalWidth = stepX * Columns - BrickGapX;
            float x = -totalWidth * 0.5f + BrickWidth * 0.5f + col * stepX;
            float y = BrickTopY - row * stepY;
            return new Vector2(x, y);
        }

        private void ShowPopup(string text, float duration)
        {
            _popupText = text;
            _popupStarted = Time.unscaledTime;
            _popupDuration = duration;
        }

        private string GetName(ProductUserId user)
        {
            if (user == null)
                return "Player";
            if (_names.TryGetValue(user.ToString(), out string name) && !string.IsNullOrWhiteSpace(name))
                return name;
            return "Player";
        }

        public void DrawGameplay()
        {
            if (!Active)
                return;

            BuildGuiStyles();
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _backgroundTex);

            float boardH = Mathf.Min(Screen.height - 86f, 760f);
            float boardW = boardH * (10f / 12f);
            float boardX = (Screen.width - boardW) * 0.5f;
            float boardY = 48f;
            Rect mainBoard = new Rect(boardX, boardY, boardW, boardH);

            string heading = GetName(_localUser);
            bool hasSpectatedState = false;
            Battle99RemoteState spectated = null;
            if (Spectating && _spectatedUser != null)
            {
                hasSpectatedState = _peer.TryGetRemoteState(_spectatedUser, out spectated) && spectated.HasSnapshot;
                heading = "SPECTATING " + SpectatedName + (hasSpectatedState && !spectated.Alive ? " : OUT" : string.Empty);
            }
            GUI.Label(new Rect(boardX, 8, boardW, 34), heading, _titleStyle);

            if (Spectating && hasSpectatedState)
                DrawSnapshotBoard(mainBoard, spectated.Snapshot, true);
            else
                DrawLocalBoard(mainBoard);

            DrawRemoteMiniBoards(mainBoard);

            if (!string.IsNullOrEmpty(_popupText))
            {
                float t = (Time.unscaledTime - _popupStarted) / Mathf.Max(0.01f, _popupDuration);
                if (t < 1f)
                {
                    Color old = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, 1f - t);
                    GUI.Label(new Rect(mainBoard.x, mainBoard.y + mainBoard.height * 0.48f - t * 24f, mainBoard.width, 52f), _popupText, _centerStyle);
                    GUI.color = old;
                }
            }

            if (_countdown > 0f && !Eliminated)
            {
                string text;
                if (_countdown > 2f) text = "3";
                else if (_countdown > 1f) text = "2";
                else if (_countdown > 0.18f) text = "1";
                else text = "START";
                GUI.Label(new Rect(mainBoard.x, mainBoard.y + mainBoard.height * 0.40f, mainBoard.width, 90f), text, _countdownStyle);
            }
        }

        private void DrawLocalBoard(Rect rect)
        {
            GUI.DrawTexture(rect, _panelTex);
            DrawBoardGrid(rect);

            for (int row = 0; row < Rows; row++)
                for (int col = 0; col < Columns; col++)
                    if (_bricks[col, row]) DrawBrick(rect, col, row, _brickTex);

            DrawPaddle(rect, Mathf.InverseLerp(-BoardHalfWidth, BoardHalfWidth, _paddleX), _paddleScale);
            for (int i = 0; i < _balls.Length; i++)
                if (_balls[i].Active) DrawBall(rect, NormalizeBoardPosition(_balls[i].Position));

            for (int i = 0; i < _pickups.Length; i++)
            {
                if (!_pickups[i].Active)
                    continue;
                Vector2 n = NormalizeBoardPosition(_pickups[i].Position);
                DrawPickup(rect, n, _pickups[i].Type == 0 ? _multiTex : _extendTex);
            }

            DrawDangerLine(rect);
            if (_pendingGarbage > 0)
                GUI.Label(new Rect(rect.x + 8, rect.y + rect.height - 34, rect.width - 16, 28), "INCOMING " + _pendingGarbage, _smallStyle);
        }

        private void DrawSnapshotBoard(Rect rect, Battle99NetSnapshot snapshot, bool large)
        {
            GUI.DrawTexture(rect, _panelTex);
            if (large) DrawBoardGrid(rect);
            for (int row = 0; row < Rows; row++)
                for (int col = 0; col < Columns; col++)
                    if (SnapshotBrickAlive(snapshot, col, row)) DrawBrick(rect, col, row, _brickTex);

            DrawPaddle(rect, snapshot.PaddleNormalized, Mathf.Max(0.5f, snapshot.PaddleScale));
            if ((snapshot.BallMask & 1) != 0) DrawBall(rect, snapshot.Ball0);
            if ((snapshot.BallMask & 2) != 0) DrawBall(rect, snapshot.Ball1);
            if ((snapshot.BallMask & 4) != 0) DrawBall(rect, snapshot.Ball2);
            if ((snapshot.BallMask & 8) != 0) DrawBall(rect, snapshot.Ball3);
            DrawDangerLine(rect);
        }

        private void DrawRemoteMiniBoards(Rect mainBoard)
        {
            int remoteCount = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                ProductUserId user = _roster[i].UserId;
                if (user != null && user.IsValid() && !Equals(user, _localUser))
                    remoteCount++;
            }

            if (remoteCount <= 0)
                return;

            int leftCount = (remoteCount + 1) / 2;
            int rightCount = remoteCount - leftCount;
            float margin = 10f;
            float sideGap = 4f;
            float top = 38f;
            float usableHeight = Mathf.Max(160f, Screen.height - top - 18f);
            float leftAreaWidth = Mathf.Max(90f, mainBoard.x - margin * 2f);
            float rightAreaWidth = Mathf.Max(90f, Screen.width - (mainBoard.x + mainBoard.width) - margin * 2f);

            DrawRemoteSide(true, 0, leftCount, leftAreaWidth, usableHeight, top, margin, sideGap);
            DrawRemoteSide(false, leftCount, rightCount, rightAreaWidth, usableHeight, top, margin, sideGap);
        }

        private void DrawRemoteSide(
            bool left,
            int rosterOffset,
            int count,
            float areaWidth,
            float areaHeight,
            float top,
            float margin,
            float gap)
        {
            if (count <= 0)
                return;

            int columns = Mathf.Clamp(Mathf.CeilToInt(count / 7f), 1, 7);
            int rows = Mathf.CeilToInt(count / (float)columns);
            float cellW = Mathf.Max(30f, (areaWidth - gap * (columns - 1)) / columns);
            float cellH = Mathf.Max(40f, (areaHeight - gap * (rows - 1)) / rows);

            float miniH = Mathf.Min(cellH - 17f, cellW * 1.2f);
            miniH = Mathf.Max(24f, miniH);
            float miniW = Mathf.Min(cellW, miniH * (10f / 12f));
            miniH = miniW * 1.2f;

            int remoteIndex = 0;
            int drawn = 0;
            for (int i = 0; i < _roster.Count; i++)
            {
                Battle99Member member = _roster[i];
                if (member.UserId == null || !member.UserId.IsValid() || Equals(member.UserId, _localUser))
                    continue;

                if (remoteIndex++ < rosterOffset)
                    continue;
                if (drawn >= count)
                    break;

                int col = drawn / rows;
                int row = drawn % rows;
                float areaX = left ? margin : Screen.width - margin - areaWidth;
                float cellX = areaX + col * (cellW + gap);
                float cellY = top + row * (cellH + gap);
                float x = cellX + (cellW - miniW) * 0.5f;
                float y = cellY + 16f;
                Rect r = new Rect(x, y, miniW, miniH);

                Battle99RemoteState state = null;
                bool hasState = _peer != null && _peer.TryGetRemoteState(member.UserId, out state);
                if (hasState && state.HasSnapshot)
                    DrawSnapshotBoard(r, state.Snapshot, false);
                else
                    GUI.DrawTexture(r, _panelTex);

                string status = hasState && !state.Alive ? " OUT" : string.Empty;
                string label = string.IsNullOrWhiteSpace(member.Name) ? "Player" : member.Name;
                if (label.Length > 12 && miniW < 80f)
                    label = label.Substring(0, 10);
                GUI.Label(new Rect(cellX, cellY, cellW, 16f), label + status, _smallStyle);
                drawn++;
            }
        }

        private void DrawBoardGrid(Rect rect)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.08f);
            for (int i = 1; i < Columns; i++)
            {
                float x = rect.x + rect.width * i / Columns;
                GUI.DrawTexture(new Rect(x, rect.y, 1f, rect.height), _brickTex);
            }
            for (int i = 1; i < Rows; i++)
            {
                float y = rect.y + rect.height * i / Rows;
                GUI.DrawTexture(new Rect(rect.x, y, rect.width, 1f), _brickTex);
            }
            GUI.color = old;
        }

        private static Rect BoardToRect(Rect rect, float normalizedX, float normalizedY, float w, float h)
        {
            float x = rect.x + normalizedX * rect.width - w * 0.5f;
            float y = rect.y + (1f - normalizedY) * rect.height - h * 0.5f;
            return new Rect(x, y, w, h);
        }

        private void DrawBrick(Rect rect, int col, int row, Texture2D texture)
        {
            Vector2 center = BrickCenter(col, row);
            Vector2 n = NormalizeBoardPosition(center);
            float w = rect.width * (BrickWidth / (BoardHalfWidth * 2f));
            float h = rect.height * (BrickHeight / BoardHeight);
            GUI.DrawTexture(BoardToRect(rect, n.x, n.y, w, h), texture);
        }

        private void DrawPaddle(Rect rect, float normalizedX, float scale)
        {
            float w = rect.width * (PaddleBaseLength * scale / (BoardHalfWidth * 2f));
            float h = Mathf.Max(3f, rect.height * (PaddleHeight / BoardHeight));
            float y = PaddleY / BoardHeight;
            GUI.DrawTexture(BoardToRect(rect, normalizedX, y, w, h), _paddleTex);
        }

        private void DrawBall(Rect rect, Vector2 normalized)
        {
            float size = Mathf.Max(4f, rect.width * (BallRadius * 2f / (BoardHalfWidth * 2f)));
            GUI.DrawTexture(BoardToRect(rect, normalized.x, normalized.y, size, size), _ballTex);
        }

        private void DrawPickup(Rect rect, Vector2 normalized, Texture2D texture)
        {
            float size = Mathf.Max(8f, rect.width * 0.055f);
            GUI.DrawTexture(BoardToRect(rect, normalized.x, normalized.y, size, size), texture);
        }

        private void DrawDangerLine(Rect rect)
        {
            float dangerWorldY = BrickCenter(0, Rows - 1).y - BrickHeight * 0.75f;
            float normalized = Mathf.InverseLerp(0f, BoardHeight, dangerWorldY);
            float y = rect.y + (1f - normalized) * rect.height;
            GUI.DrawTexture(new Rect(rect.x, y, rect.width, Mathf.Max(1f, rect.height * 0.003f)), _dangerTex);
        }

        private void BuildGuiStyles()
        {
            if (_titleStyle != null)
                return;

            _backgroundTex = MakeTex(new Color(0.015f, 0.02f, 0.035f, 1f));
            _panelTex = MakeTex(new Color(0.025f, 0.04f, 0.065f, 1f));
            _brickTex = MakeTex(new Color(0.72f, 0.76f, 0.82f, 1f));
            _garbageTex = MakeTex(new Color(0.48f, 0.50f, 0.55f, 1f));
            _paddleTex = MakeTex(new Color(0.20f, 0.78f, 0.90f, 1f));
            _ballTex = MakeTex(Color.white);
            _multiTex = MakeTex(new Color(0.72f, 0.42f, 0.88f, 1f));
            _extendTex = MakeTex(new Color(0.30f, 0.78f, 0.42f, 1f));
            _dangerTex = MakeTex(new Color(0.92f, 0.28f, 0.32f, 1f));

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = Color.white }
            };
            _smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.78f, 0.82f, 0.9f) }
            };
            _centerStyle = new GUIStyle(_titleStyle)
            {
                fontSize = 30
            };
            _countdownStyle = new GUIStyle(_titleStyle)
            {
                fontSize = 64,
                fontStyle = FontStyle.Bold
            };
        }

        private static Texture2D MakeTex(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}

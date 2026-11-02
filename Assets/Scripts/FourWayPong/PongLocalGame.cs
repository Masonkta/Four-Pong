using UnityEngine;

namespace FourWayPong
{
    public sealed class PongLocalGame : MonoBehaviour
    {
        public bool Active { get; private set; }
        public bool Paused { get; private set; }
        public int Player1Lives { get; private set; }
        public int Player2Lives { get; private set; }
        public int Winner { get; private set; }
        public PongGameMode Mode { get; private set; } = PongGameMode.Classic;

        public float CurrentBallSpeed
        {
            get
            {
                float speed = 0f;
                for (int i = 0; i < PongConfig.MaxBalls; i++)
                    if (_ballActive[i])
                        speed = Mathf.Max(speed, _ballVelocity[i].magnitude);
                return speed;
            }
        }

        private float _leftPaddle;
        private float _rightPaddle;
        private float _leftScale = 1f;
        private float _rightScale = 1f;
        private readonly Vector2[] _ballPosition = new Vector2[PongConfig.MaxBalls];
        private readonly Vector2[] _ballVelocity = new Vector2[PongConfig.MaxBalls];
        private readonly bool[] _ballActive = new bool[PongConfig.MaxBalls];
        private readonly int[] _ballLastHitter = new int[PongConfig.MaxBalls];
        private readonly bool[] _brickAlive = new bool[PongConfig.BrickColumns * PongConfig.BrickRows];
        private readonly byte[] _brickPowerups = new byte[PongConfig.BrickColumns * PongConfig.BrickRows];
        private readonly bool[] _powerupActive = new bool[PongConfig.MaxPowerupPickups];
        private readonly byte[] _powerupType = new byte[PongConfig.MaxPowerupPickups];
        private readonly Vector2[] _powerupPosition = new Vector2[PongConfig.MaxPowerupPickups];
        private readonly float[] _powerupArmAt = new float[PongConfig.MaxPowerupPickups];
        private readonly float[] _powerupExpireAt = new float[PongConfig.MaxPowerupPickups];
        private float _serveDelay;
        private float _rallyTime;
        private int _serveDirection = 1;
        private int _servePlayer;
        private int _remainingBricks;
        private bool _brickResetPending;
        private bool _brickVisualDirty = true;
        private PongGameMode _lastVisualMode = (PongGameMode)(-1);
        private int _lastVisualWinner = -1;

        private GameObject _root;
        private readonly GameObject[] _ballVisuals = new GameObject[PongConfig.MaxBalls];
        private readonly Renderer[] _ballRenderers = new Renderer[PongConfig.MaxBalls];
        private readonly int[] _lastBallOwner = { -1, -1, -1, -1 };
        private readonly GameObject[] _brickVisuals = new GameObject[PongConfig.BrickColumns * PongConfig.BrickRows];
        private readonly GameObject[] _powerupVisuals = new GameObject[PongConfig.MaxPowerupPickups];
        private readonly Renderer[] _powerupRenderers = new Renderer[PongConfig.MaxPowerupPickups];
        private GameObject _leftVisual;
        private GameObject _rightVisual;
        private GameObject _topWall;
        private GameObject _bottomWall;
        private Material _p1Material;
        private Material _p2Material;
        private Material _whiteMaterial;
        private Material _wallMaterial;
        private Material _brickMaterial;
        private readonly Material[] _powerupMaterials = new Material[4];

        private struct GameplayPopup
        {
            public bool Active;
            public string Text;
            public int Team;
            public float StartTime;
            public float Duration;
            public bool IsScore;
        }

        private readonly GameplayPopup[] _gameplayPopups = new GameplayPopup[8];
        private float _countdownStartTime = -1f;
        private float _countdownPauseStarted = -1f;
        private float _countdownPausedTotal;
        private GUIStyle _gameplayPopupStyle;
        private GUIStyle _countdownStyle;

        public void StartMatch()
        {
            StartMatch(PongGameMode.Classic);
        }

        public void StartMatch(PongGameMode mode)
        {
            EnsureVisuals();
            Mode = mode;
            Active = true;
            Paused = false;
            Winner = 0;
            Player1Lives = PongConfig.DefaultStartingLives;
            Player2Lives = PongConfig.DefaultStartingLives;
            _leftPaddle = 0f;
            _rightPaddle = 0f;
            _leftScale = 1f;
            _rightScale = 1f;
            _serveDirection = Random.value < 0.5f ? -1 : 1;
            _servePlayer = 0;
            _countdownStartTime = -1f;
            _brickResetPending = false;
            ClearPowerupPickups();
            if (Mode == PongGameMode.BrickWar)
                ResetBricks();
            else
                ClearBricks();

            _root.SetActive(true);
            QueueServe();
            _brickVisualDirty = true;
            UpdateVisuals();
        }

        public void StopMatch()
        {
            Active = false;
            Paused = false;
            _countdownStartTime = -1f;
            if (_root != null)
                _root.SetActive(false);
        }

        public void RestartMatch()
        {
            StartMatch(Mode);
        }

        public void SetPaused(bool paused)
        {
            if (!Active || Winner != 0)
                paused = false;
            Paused = paused;
        }

        private void Update()
        {
            if (!Active)
                return;

            if (Paused)
            {
                UpdateVisuals();
                return;
            }

            CaptureInput(Time.deltaTime);

            if (Winner != 0)
            {
                UpdateVisuals();
                return;
            }

            if (_serveDelay > 0f)
            {
                if (Mode == PongGameMode.BrickWar && _servePlayer != 0)
                    _ballPosition[0] = GetServeSpawnPosition(_servePlayer);

                _serveDelay -= Time.deltaTime;
                if (_serveDelay <= 0f)
                    LaunchServe();
                UpdateVisuals();
                return;
            }

            TickBalls(Time.deltaTime);
            UpdateVisuals();
        }

        private void CaptureInput(float dt)
        {
            float leftLength = PongConfig.PaddleLong * _leftScale;
            float rightLength = PongConfig.PaddleLong * _rightScale;
            float leftLimit = PongConfig.HalfHeight - leftLength * 0.5f - 0.25f;
            float rightLimit = PongConfig.HalfHeight - rightLength * 0.5f - 0.25f;

            float p1;
            if (PongSettings.Player1UsesMouse)
            {
                p1 = GetMouseInput(_leftPaddle, leftLimit);
            }
            else
            {
                p1 = 0f;
                if (Input.GetKey(PongSettings.Player1Up)) p1 += 1f;
                if (Input.GetKey(PongSettings.Player1Down)) p1 -= 1f;
            }

            float p2;
            if (PongSettings.Player2UsesMouse)
            {
                p2 = GetMouseInput(_rightPaddle, rightLimit);
            }
            else
            {
                p2 = 0f;
                if (Input.GetKey(PongSettings.Player2Up)) p2 += 1f;
                if (Input.GetKey(PongSettings.Player2Down)) p2 -= 1f;
            }

            float currentBallSpeed = Mathf.Max(PongConfig.BallStartSpeed, CurrentBallSpeed);
            float ballSpeedRatio = currentBallSpeed / PongConfig.BallStartSpeed;
            float paddleSpeed = PongConfig.PaddleSpeed * Mathf.Lerp(1f, ballSpeedRatio, PongConfig.PaddleSpeedScaling);

            _leftPaddle = Mathf.Clamp(_leftPaddle + p1 * paddleSpeed * dt, -leftLimit, leftLimit);
            _rightPaddle = Mathf.Clamp(_rightPaddle + p2 * paddleSpeed * dt, -rightLimit, rightLimit);
        }

        private static float GetMouseInput(float currentPosition, float limit)
        {
            float normalized = Screen.height > 1f
                ? Mathf.Clamp01(Input.mousePosition.y / Screen.height)
                : 0.5f;

            float target = Mathf.Lerp(-limit, limit, normalized);
            float difference = target - currentPosition;
            if (Mathf.Abs(difference) < 0.04f)
                return 0f;

            return Mathf.Clamp(difference / 0.75f, -1f, 1f);
        }

        private void TickBalls(float dt)
        {
            _rallyTime += dt;
            float targetSpeed = PongConfig.BallStartSpeed + _rallyTime * PongConfig.BallAccelerationPerSecond;

            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                if (!_ballActive[ball])
                    continue;

                if (_ballVelocity[ball].sqrMagnitude <= 0.001f)
                    continue;

                _ballVelocity[ball] = _ballVelocity[ball].normalized * targetSpeed;
                int steps = Mathf.Max(1, Mathf.CeilToInt(targetSpeed * dt / PongConfig.BallSimulationStepDistance));
                float stepDt = dt / steps;

                for (int i = 0; i < steps && _ballActive[ball]; i++)
                    TickBallStep(ball, stepDt);
            }

            if (Winner == 0 && CountActiveBalls() == 0 && _serveDelay <= 0f)
                QueueServe();
        }

        private void TickBallStep(int ball, float dt)
        {
            Vector2 pos = _ballPosition[ball] + _ballVelocity[ball] * dt;
            Vector2 vel = _ballVelocity[ball];
            float paddleX = PongConfig.HalfWidth - PongConfig.PaddleInset;
            float shortHalf = PongConfig.PaddleShort * 0.5f;

            float leftHalf = PongConfig.PaddleLong * _leftScale * 0.5f;
            if (vel.x < 0f && pos.x - PongConfig.BallRadius <= -paddleX + shortHalf && pos.x > -PongConfig.HalfWidth)
            {
                float offset = pos.y - _leftPaddle;
                if (Mathf.Abs(offset) <= leftHalf + PongConfig.BallRadius)
                {
                    pos.x = -paddleX + shortHalf + PongConfig.BallRadius;
                    vel = ReflectVertical(vel, offset / Mathf.Max(0.01f, leftHalf), 1f);
                    _ballLastHitter[ball] = 1;
                    PongAudio.PlayPaddle();
                }
            }

            float rightHalf = PongConfig.PaddleLong * _rightScale * 0.5f;
            if (vel.x > 0f && pos.x + PongConfig.BallRadius >= paddleX - shortHalf && pos.x < PongConfig.HalfWidth)
            {
                float offset = pos.y - _rightPaddle;
                if (Mathf.Abs(offset) <= rightHalf + PongConfig.BallRadius)
                {
                    pos.x = paddleX - shortHalf - PongConfig.BallRadius;
                    vel = ReflectVertical(vel, offset / Mathf.Max(0.01f, rightHalf), -1f);
                    _ballLastHitter[ball] = 2;
                    PongAudio.PlayPaddle();
                }
            }

            if (Mode == PongGameMode.BrickWar)
            {
                ResolveBrickCollision(ball, ref pos, ref vel);
                TryCollectPowerup(ball, pos, vel);
            }

            if (pos.y + PongConfig.BallRadius >= PongConfig.HalfHeight && vel.y > 0f)
            {
                pos.y = PongConfig.HalfHeight - PongConfig.BallRadius;
                vel.y = -Mathf.Abs(vel.y);
                PongAudio.PlayWall();
            }
            else if (pos.y - PongConfig.BallRadius <= -PongConfig.HalfHeight && vel.y < 0f)
            {
                pos.y = -PongConfig.HalfHeight + PongConfig.BallRadius;
                vel.y = Mathf.Abs(vel.y);
                PongAudio.PlayWall();
            }

            if (pos.x < -PongConfig.HalfWidth - PongConfig.BallRadius)
            {
                LoseLife(1, ball);
                return;
            }

            if (pos.x > PongConfig.HalfWidth + PongConfig.BallRadius)
            {
                LoseLife(2, ball);
                return;
            }

            _ballPosition[ball] = pos;
            _ballVelocity[ball] = vel;
        }

        private static Vector2 ReflectVertical(Vector2 current, float hitOffset, float direction)
        {
            float speed = Mathf.Max(PongConfig.BallStartSpeed, current.magnitude);
            Vector2 v = new Vector2(direction, Mathf.Clamp(hitOffset, -0.88f, 0.88f) * 0.95f).normalized * speed;
            if (Mathf.Abs(v.x) < speed * 0.44f)
                v.x = direction * speed * 0.44f;
            return v.normalized * speed;
        }

        private void LoseLife(int player, int ball)
        {
            PongAudio.PlayScore();

            int scorer = ball >= 0 && ball < _ballLastHitter.Length ? _ballLastHitter[ball] : 0;
            if (scorer == 1)
                AddGameplayPopup("PLAYER 1 SCORES!", 0, PongConfig.ScorePopupDuration, true);
            else if (scorer == 2)
                AddGameplayPopup("PLAYER 2 SCORES!", 1, PongConfig.ScorePopupDuration, true);
            else
                AddGameplayPopup("SCORE!", -1, PongConfig.ScorePopupDuration, true);

            _ballActive[ball] = false;
            _ballVelocity[ball] = Vector2.zero;

            if (player == 1)
                Player1Lives = Mathf.Max(0, Player1Lives - 1);
            else
                Player2Lives = Mathf.Max(0, Player2Lives - 1);

            if (Player1Lives <= 0)
            {
                Winner = 2;
                _brickVisualDirty = true;
                ClearBalls();
                return;
            }

            if (Player2Lives <= 0)
            {
                Winner = 1;
                _brickVisualDirty = true;
                ClearBalls();
                return;
            }

            _serveDirection = player == 1 ? -1 : 1;
        }

        private void QueueServe()
        {
            if (Mode == PongGameMode.BrickWar && _brickResetPending)
                ResetBricks();

            ClearPowerupPickups();
            ClearBalls();
            _ballActive[0] = true;

            if (Mode == PongGameMode.BrickWar)
            {
                _servePlayer = Random.value < 0.5f ? 1 : 2;
                _ballPosition[0] = GetServeSpawnPosition(_servePlayer);
                _ballLastHitter[0] = _servePlayer;
            }
            else
            {
                _servePlayer = 0;
                _ballPosition[0] = Vector2.zero;
                _ballLastHitter[0] = 0;
            }

            _ballVelocity[0] = Vector2.zero;
            _rallyTime = 0f;
            _serveDelay = PongConfig.ServeDelay;
            _countdownStartTime = Time.unscaledTime;
            _countdownPauseStarted = -1f;
            _countdownPausedTotal = 0f;
        }

        private void LaunchServe()
        {
            _ballActive[0] = true;

            if (Mode == PongGameMode.BrickWar && _servePlayer != 0)
            {
                Vector2 spawn = GetServeSpawnPosition(_servePlayer);
                Vector2 direction = (-spawn).normalized;
                Vector2 tangent = new Vector2(-direction.y, direction.x);
                direction = (direction + tangent * Random.Range(-0.28f, 0.28f)).normalized;
                _ballPosition[0] = spawn;
                _ballVelocity[0] = direction * PongConfig.BallStartSpeed;
            }
            else
            {
                float y = Random.Range(-0.62f, 0.62f);
                if (Mathf.Abs(y) < 0.20f)
                    y = 0.20f * (Random.value < 0.5f ? -1f : 1f);
                _ballPosition[0] = Vector2.zero;
                _ballVelocity[0] = new Vector2(_serveDirection, y).normalized * PongConfig.BallStartSpeed;
            }

            _rallyTime = 0f;
        }

        private Vector2 GetServeSpawnPosition(int player)
        {
            float planeX = PongConfig.HalfWidth - PongConfig.PaddleInset;
            float inward = PongConfig.PaddleShort * 0.5f + PongConfig.BallRadius + PongConfig.ServeSpawnGap;
            return player == 1
                ? new Vector2(-planeX + inward, _leftPaddle)
                : new Vector2(planeX - inward, _rightPaddle);
        }

        private void ClearBalls()
        {
            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                _ballActive[i] = false;
                _ballPosition[i] = Vector2.zero;
                _ballVelocity[i] = Vector2.zero;
                _ballLastHitter[i] = 0;
            }
        }

        private int CountActiveBalls()
        {
            int count = 0;
            for (int i = 0; i < PongConfig.MaxBalls; i++)
                if (_ballActive[i])
                    count++;
            return count;
        }

        private void ResetBricks()
        {
            int center = (PongConfig.BrickRows / 2) * PongConfig.BrickColumns + PongConfig.BrickColumns / 2;
            _remainingBricks = 0;
            _brickResetPending = false;

            for (int i = 0; i < _brickAlive.Length; i++)
            {
                _brickAlive[i] = i != center;
                _brickPowerups[i] = 0;
                if (!_brickAlive[i])
                    continue;

                _remainingBricks++;
                if (Random.value < PongConfig.BrickPowerupChance)
                    _brickPowerups[i] = (byte)Random.Range(1, 4);
            }

            _brickVisualDirty = true;
        }

        private void ClearBricks()
        {
            for (int i = 0; i < _brickAlive.Length; i++)
            {
                _brickAlive[i] = false;
                _brickPowerups[i] = 0;
            }

            _remainingBricks = 0;
            _brickResetPending = false;
            _brickVisualDirty = true;
        }

        private void ResolveBrickCollision(int ball, ref Vector2 pos, ref Vector2 vel)
        {
            if (Mode != PongGameMode.BrickWar || _remainingBricks <= 0)
                return;

            float stepX = PongConfig.BrickWidth + PongConfig.BrickGap;
            float stepY = PongConfig.BrickHeight + PongConfig.BrickGap;
            float offsetX = (PongConfig.BrickColumns - 1) * 0.5f;
            float offsetY = (PongConfig.BrickRows - 1) * 0.5f;
            int centerColumn = Mathf.RoundToInt(pos.x / stepX + offsetX);
            int centerRow = Mathf.RoundToInt(pos.y / stepY + offsetY);

            int minColumn = Mathf.Max(0, centerColumn - 1);
            int maxColumn = Mathf.Min(PongConfig.BrickColumns - 1, centerColumn + 1);
            int minRow = Mathf.Max(0, centerRow - 1);
            int maxRow = Mathf.Min(PongConfig.BrickRows - 1, centerRow + 1);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int index = row * PongConfig.BrickColumns + column;
                    if (TryResolveBrickAt(index, ball, ref pos, ref vel))
                        return;
                }
            }
        }

        private bool TryResolveBrickAt(int index, int ball, ref Vector2 pos, ref Vector2 vel)
        {
            if (!_brickAlive[index])
                return false;

            float halfW = PongConfig.BrickWidth * 0.5f;
            float halfH = PongConfig.BrickHeight * 0.5f;
            Vector2 center = GetBrickCenter(index);
            float dx = pos.x - center.x;
            float dy = pos.y - center.y;
            float overlapX = halfW + PongConfig.BallRadius - Mathf.Abs(dx);
            float overlapY = halfH + PongConfig.BallRadius - Mathf.Abs(dy);
            if (overlapX <= 0f || overlapY <= 0f)
                return false;

            _brickAlive[index] = false;
            _remainingBricks = Mathf.Max(0, _remainingBricks - 1);
            if (_remainingBricks == 0)
                _brickResetPending = true;
            _brickVisualDirty = true;

            if (overlapX < overlapY)
            {
                float sign = Mathf.Abs(dx) > 0.0001f ? Mathf.Sign(dx) : (vel.x > 0f ? -1f : 1f);
                pos.x = center.x + sign * (halfW + PongConfig.BallRadius);
                vel.x = -vel.x;
            }
            else
            {
                float sign = Mathf.Abs(dy) > 0.0001f ? Mathf.Sign(dy) : (vel.y > 0f ? -1f : 1f);
                pos.y = center.y + sign * (halfH + PongConfig.BallRadius);
                vel.y = -vel.y;
            }

            PongAudio.PlayWall();
            byte powerup = _brickPowerups[index];
            _brickPowerups[index] = 0;
            if (powerup != 0)
                SpawnPowerupPickup(powerup, GetBrickCenter(index));
            return true;
        }

        private void SpawnPowerupPickup(byte type, Vector2 position)
        {
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                if (_powerupActive[i])
                    continue;

                _powerupActive[i] = true;
                _powerupType[i] = type;
                _powerupPosition[i] = position;
                _powerupArmAt[i] = Time.time + PongConfig.PowerupArmDelay;
                _powerupExpireAt[i] = float.PositiveInfinity;
                SetPowerupVisual(i, type, position, true);
                return;
            }
        }

        private void UpdatePowerupPickups()
        {
            float now = Time.time;
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                if (_powerupActive[i] && now >= _powerupExpireAt[i])
                    RemovePowerupPickup(i);
            }
        }

        private void TryCollectPowerup(int ball, Vector2 position, Vector2 velocity)
        {
            float now = Time.time;
            float distance = PongConfig.PowerupPickupRadius + PongConfig.BallRadius;
            float distanceSqr = distance * distance;

            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                if (!_powerupActive[i] || now < _powerupArmAt[i])
                    continue;

                if ((position - _powerupPosition[i]).sqrMagnitude > distanceSqr)
                    continue;

                byte type = _powerupType[i];
                RemovePowerupPickup(i);
                ApplyCollectedPowerup(type, ball, position, velocity);
                return;
            }
        }

        private void ApplyCollectedPowerup(byte powerup, int sourceBall, Vector2 pos, Vector2 vel)
        {
            int hitter = _ballLastHitter[sourceBall];
            int team = hitter == 1 ? 0 : hitter == 2 ? 1 : -1;
            bool applied = false;
            string popupText = string.Empty;

            if (powerup == 1)
            {
                applied = DuplicateBall(sourceBall, pos, vel);
                popupText = "MULTIBALL";
            }
            else if (powerup == 2 && hitter != 0)
            {
                if (hitter == 1)
                    _leftScale = Mathf.Clamp(_leftScale + PongConfig.AllyGrowAmount, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
                else
                    _rightScale = Mathf.Clamp(_rightScale + PongConfig.AllyGrowAmount, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
                applied = true;
                popupText = "PADDLE EXTEND";
            }
            else if (powerup == 3 && hitter != 0)
            {
                if (hitter == 1)
                    _rightScale = Mathf.Clamp(_rightScale - PongConfig.EnemyShrinkAmount, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
                else
                    _leftScale = Mathf.Clamp(_leftScale - PongConfig.EnemyShrinkAmount, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
                applied = true;
                popupText = "PADDLE SHRINK";
            }

            if (applied && !string.IsNullOrEmpty(popupText))
                AddGameplayPopup(popupText, team, PongConfig.GameplayPopupDuration, false);
        }

        private void RemovePowerupPickup(int slot)
        {
            if (slot < 0 || slot >= PongConfig.MaxPowerupPickups)
                return;

            _powerupActive[slot] = false;
            _powerupType[slot] = 0;
            _powerupPosition[slot] = Vector2.zero;
            _powerupArmAt[slot] = 0f;
            _powerupExpireAt[slot] = 0f;
            SetPowerupVisual(slot, 0, Vector2.zero, false);
        }

        private void ClearPowerupPickups()
        {
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
                RemovePowerupPickup(i);
        }

        private bool DuplicateBall(int sourceBall, Vector2 pos, Vector2 vel)
        {
            int target = -1;
            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                if (!_ballActive[i])
                {
                    target = i;
                    break;
                }
            }

            if (target < 0)
                return false;

            float angle = Random.value < 0.5f ? -32f : 32f;
            float radians = angle * Mathf.Deg2Rad;
            Vector2 direction = vel.sqrMagnitude > 0.001f ? vel.normalized : Vector2.right;
            Vector2 rotated = new Vector2(
                direction.x * Mathf.Cos(radians) - direction.y * Mathf.Sin(radians),
                direction.x * Mathf.Sin(radians) + direction.y * Mathf.Cos(radians));

            float speed = Mathf.Max(PongConfig.BallStartSpeed, vel.magnitude);
            _ballActive[target] = true;
            _ballPosition[target] = pos + rotated.normalized * (PongConfig.BallRadius * 1.2f);
            _ballVelocity[target] = rotated.normalized * speed;
            _ballLastHitter[target] = _ballLastHitter[sourceBall];
            return true;
        }

        private static Vector2 GetBrickCenter(int index)
        {
            int column = index % PongConfig.BrickColumns;
            int row = index / PongConfig.BrickColumns;
            float stepX = PongConfig.BrickWidth + PongConfig.BrickGap;
            float stepY = PongConfig.BrickHeight + PongConfig.BrickGap;
            float x = (column - (PongConfig.BrickColumns - 1) * 0.5f) * stepX;
            float y = (row - (PongConfig.BrickRows - 1) * 0.5f) * stepY;
            return new Vector2(x, y);
        }

        private void AddGameplayPopup(string text, int team, float duration, bool isScore)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            int slot = -1;
            float oldest = float.MaxValue;
            int oldestSlot = 0;
            for (int i = 0; i < _gameplayPopups.Length; i++)
            {
                if (!_gameplayPopups[i].Active)
                {
                    slot = i;
                    break;
                }

                if (_gameplayPopups[i].StartTime < oldest)
                {
                    oldest = _gameplayPopups[i].StartTime;
                    oldestSlot = i;
                }
            }

            if (slot < 0)
                slot = oldestSlot;

            _gameplayPopups[slot] = new GameplayPopup
            {
                Active = true,
                Text = text,
                Team = team,
                StartTime = Time.unscaledTime,
                Duration = Mathf.Max(0.25f, duration),
                IsScore = isScore
            };
        }

        private void EnsureOverlayStyles()
        {
            if (_gameplayPopupStyle == null)
            {
                _gameplayPopupStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false
                };
            }

            if (_countdownStyle == null)
            {
                _countdownStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false
                };
            }
        }

        private void OnGUI()
        {
            if (!Active)
                return;

            EnsureOverlayStyles();
            DrawServeCountdown();
            DrawGameplayPopups();
        }

        private void DrawServeCountdown()
        {
            if (_countdownStartTime < 0f)
                return;

            float now = Time.unscaledTime;
            if (Paused)
            {
                if (_countdownPauseStarted < 0f)
                    _countdownPauseStarted = now;
            }
            else if (_countdownPauseStarted >= 0f)
            {
                _countdownPausedTotal += now - _countdownPauseStarted;
                _countdownPauseStarted = -1f;
            }

            float effectiveNow = Paused && _countdownPauseStarted >= 0f ? _countdownPauseStarted : now;
            float elapsed = effectiveNow - _countdownStartTime - _countdownPausedTotal;
            if (elapsed >= PongConfig.ServeDelay)
            {
                _countdownStartTime = -1f;
                return;
            }

            float numberDuration = PongConfig.ServeCountdownNumberDuration;
            string text;
            float segmentStart;
            float segmentDuration;

            if (elapsed < numberDuration)
            {
                text = "3";
                segmentStart = 0f;
                segmentDuration = numberDuration;
            }
            else if (elapsed < numberDuration * 2f)
            {
                text = "2";
                segmentStart = numberDuration;
                segmentDuration = numberDuration;
            }
            else if (elapsed < numberDuration * 3f)
            {
                text = "1";
                segmentStart = numberDuration * 2f;
                segmentDuration = numberDuration;
            }
            else
            {
                text = "START";
                segmentStart = numberDuration * 3f;
                segmentDuration = PongConfig.ServeCountdownStartDuration;
            }

            float progress = Mathf.Clamp01((elapsed - segmentStart) / Mathf.Max(0.01f, segmentDuration));
            float alpha = 1f - Mathf.SmoothStep(0f, 1f, progress);
            float scale = Mathf.Lerp(1.18f, 0.88f, progress);
            int fontSize = text == "START"
                ? Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.085f, 42f, 90f))
                : Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.16f, 76f, 170f));

            _countdownStyle.fontSize = fontSize;
            _countdownStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);

            Rect rect = new Rect(0f, Screen.height * 0.31f, Screen.width, Screen.height * 0.30f);
            Matrix4x4 oldMatrix = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), rect.center);
            GUI.Label(rect, text, _countdownStyle);
            GUI.matrix = oldMatrix;
        }

        private void DrawGameplayPopups()
        {
            float now = Time.unscaledTime;
            int powerupRow = 0;

            for (int i = 0; i < _gameplayPopups.Length; i++)
            {
                GameplayPopup popup = _gameplayPopups[i];
                if (!popup.Active)
                    continue;

                float progress = (now - popup.StartTime) / Mathf.Max(0.01f, popup.Duration);
                if (progress >= 1f)
                {
                    popup.Active = false;
                    _gameplayPopups[i] = popup;
                    continue;
                }

                float alpha = 1f - Mathf.SmoothStep(0.42f, 1f, progress);
                float intro = Mathf.Clamp01(progress / 0.14f);
                float popScale = Mathf.Lerp(0.72f, 1.12f, intro);
                if (progress > 0.14f)
                    popScale = Mathf.Lerp(1.12f, 1f, Mathf.Clamp01((progress - 0.14f) / 0.16f));

                Color color = popup.Team >= 0 ? PongMatch.TeamColor(popup.Team) : Color.white;
                color.a = alpha;
                _gameplayPopupStyle.normal.textColor = color;
                _gameplayPopupStyle.fontSize = popup.IsScore
                    ? Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.070f, 34f, 68f))
                    : Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.046f, 24f, 48f));

                float baseY;
                if (popup.IsScore)
                {
                    baseY = Screen.height * 0.23f;
                }
                else
                {
                    baseY = Screen.height * 0.39f + powerupRow * 42f;
                    powerupRow++;
                }

                float rise = progress * (popup.IsScore ? 34f : 48f);
                Rect rect = new Rect(0f, baseY - rise, Screen.width, 90f);
                Matrix4x4 oldMatrix = GUI.matrix;
                GUIUtility.ScaleAroundPivot(new Vector2(popScale, popScale), rect.center);
                GUI.Label(rect, popup.Text, _gameplayPopupStyle);
                GUI.matrix = oldMatrix;
            }
        }

        private void EnsureVisuals()
        {
            if (_root != null)
                return;

            _root = new GameObject("Local Pong Visuals");
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            _whiteMaterial = new Material(shader) { color = new Color(0.94f, 0.97f, 1f) };
            _wallMaterial = new Material(shader) { color = new Color(0.18f, 0.22f, 0.30f) };
            _brickMaterial = new Material(shader) { color = new Color(0.86f, 0.52f, 0.22f) };
            _p1Material = new Material(shader) { color = PongMatch.TeamColor(0) };
            _p2Material = new Material(shader) { color = PongMatch.TeamColor(1) };
            _powerupMaterials[1] = new Material(shader) { color = new Color(0.62f, 0.42f, 1.00f) };
            _powerupMaterials[2] = new Material(shader) { color = new Color(0.35f, 1.00f, 0.55f) };
            _powerupMaterials[3] = new Material(shader) { color = new Color(1.00f, 0.32f, 0.32f) };

            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                _ballVisuals[i] = Primitive("Local Ball " + (i + 1), PrimitiveType.Sphere, _whiteMaterial);
                _ballVisuals[i].transform.SetParent(_root.transform, true);
                _ballVisuals[i].transform.localScale = Vector3.one * PongConfig.BallRadius * 2f;
                _ballRenderers[i] = _ballVisuals[i].GetComponent<Renderer>();
            }

            _leftVisual = Primitive("Player 1 Paddle", PrimitiveType.Cube, _p1Material);
            _rightVisual = Primitive("Player 2 Paddle", PrimitiveType.Cube, _p2Material);
            _topWall = Primitive("Top Wall", PrimitiveType.Cube, _wallMaterial);
            _bottomWall = Primitive("Bottom Wall", PrimitiveType.Cube, _wallMaterial);

            foreach (Transform child in new[] { _leftVisual.transform, _rightVisual.transform, _topWall.transform, _bottomWall.transform })
                child.SetParent(_root.transform, true);

            for (int i = 0; i < _brickVisuals.Length; i++)
            {
                _brickVisuals[i] = Primitive("Local Brick " + i, PrimitiveType.Cube, _brickMaterial);
                _brickVisuals[i].transform.SetParent(_root.transform, true);
                Vector2 center = GetBrickCenter(i);
                _brickVisuals[i].transform.position = new Vector3(center.x, center.y, 0.2f);
                _brickVisuals[i].transform.localScale = new Vector3(PongConfig.BrickWidth, PongConfig.BrickHeight, PongConfig.BrickDepth);
            }

            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                _powerupVisuals[i] = Primitive("Local Powerup " + (i + 1), PrimitiveType.Sphere, _whiteMaterial);
                _powerupVisuals[i].transform.SetParent(_root.transform, true);
                _powerupRenderers[i] = _powerupVisuals[i].GetComponent<Renderer>();
                _powerupVisuals[i].SetActive(false);
            }

            _topWall.transform.localScale = new Vector3(PongConfig.HalfWidth * 2f, 0.12f, 0.12f);
            _bottomWall.transform.localScale = new Vector3(PongConfig.HalfWidth * 2f, 0.12f, 0.12f);
            _topWall.transform.position = new Vector3(0f, PongConfig.HalfHeight, 0.25f);
            _bottomWall.transform.position = new Vector3(0f, -PongConfig.HalfHeight, 0.25f);
        }

        private static GameObject Primitive(string name, PrimitiveType type, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            Object.Destroy(obj.GetComponent<Collider>());
            obj.GetComponent<Renderer>().sharedMaterial = material;
            return obj;
        }

        private void SetPowerupVisual(int slot, int type, Vector2 position, bool active)
        {
            if (slot < 0 || slot >= PongConfig.MaxPowerupPickups || _powerupVisuals[slot] == null)
                return;

            GameObject visual = _powerupVisuals[slot];
            if (!active)
            {
                if (visual.activeSelf)
                    visual.SetActive(false);
                return;
            }

            visual.transform.position = new Vector3(position.x, position.y, -0.16f);
            visual.transform.localScale = Vector3.one * (PongConfig.PowerupPickupRadius * 2f);
            if (_powerupRenderers[slot] != null)
                _powerupRenderers[slot].sharedMaterial = type >= 1 && type <= 3 ? _powerupMaterials[type] : _whiteMaterial;

            if (!visual.activeSelf)
                visual.SetActive(true);
        }

        private void UpdatePowerupVisuals()
        {
            bool show = Active && Winner == 0 && Mode == PongGameMode.BrickWar;
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                GameObject visual = _powerupVisuals[i];
                if (visual == null)
                    continue;

                bool active = show && _powerupActive[i];
                if (visual.activeSelf != active)
                    visual.SetActive(active);

                if (!active)
                    continue;

                float pulse = 1f + Mathf.Sin(Time.time * 5f + i * 0.37f) * 0.12f;
                float diameter = PongConfig.PowerupPickupRadius * 2f * pulse;
                visual.transform.localScale = Vector3.one * diameter;
                visual.transform.Rotate(0f, 0f, 70f * Time.deltaTime, Space.Self);
            }
        }

        private void UpdateVisuals()
        {
            if (_root == null)
                return;

            float x = PongConfig.HalfWidth - PongConfig.PaddleInset;
            _leftVisual.transform.position = new Vector3(-x, _leftPaddle, 0f);
            _rightVisual.transform.position = new Vector3(x, _rightPaddle, 0f);
            _leftVisual.transform.localScale = new Vector3(PongConfig.PaddleShort, PongConfig.PaddleLong * _leftScale, 0.35f);
            _rightVisual.transform.localScale = new Vector3(PongConfig.PaddleShort, PongConfig.PaddleLong * _rightScale, 0.35f);

            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                _ballVisuals[i].transform.position = new Vector3(_ballPosition[i].x, _ballPosition[i].y, 0f);
                _ballVisuals[i].SetActive(_ballActive[i] && Winner == 0);

                int owner = _ballLastHitter[i];
                if (_lastBallOwner[i] != owner && _ballRenderers[i] != null)
                {
                    _lastBallOwner[i] = owner;
                    _ballRenderers[i].sharedMaterial = owner == 1 ? _p1Material : owner == 2 ? _p2Material : _whiteMaterial;
                }
            }

            UpdatePowerupVisuals();

            if (_brickVisualDirty || _lastVisualMode != Mode || _lastVisualWinner != Winner)
            {
                bool showBricks = Mode == PongGameMode.BrickWar && Winner == 0;
                for (int i = 0; i < _brickVisuals.Length; i++)
                {
                    bool active = showBricks && _brickAlive[i];
                    if (_brickVisuals[i].activeSelf != active)
                        _brickVisuals[i].SetActive(active);
                }

                _brickVisualDirty = false;
                _lastVisualMode = Mode;
                _lastVisualWinner = Winner;
            }
        }
    }
}

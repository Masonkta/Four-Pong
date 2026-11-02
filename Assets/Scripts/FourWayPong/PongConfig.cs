namespace FourWayPong
{
    public enum PaddleSeat
    {
        Left = 0,
        Right = 1,
        Top = 2,
        Bottom = 3
    }

    public enum PongGameMode
    {
        Classic = 0,
        BrickWar = 1
    }

    public static class PongConfig
    {
        public const int MaxPlayers = 4;

        public const int Battle99MaxPlayers = 99;
        public const int Battle99MinPlayers = 2;

        public const int DefaultStartingLives = 7;
        public const int MinStartingLives = 1;
        public const int MaxStartingLives = 20;

        public const float HalfWidth = 10f;
        public const float HalfHeight = 6f;
        public const float PaddleInset = 0.55f;
        public const float PaddleLong = 2.6f;
        public const float HorizontalPaddleLong = PaddleLong * (HalfWidth / HalfHeight);
        public const float PaddleShort = 0.34f;
        public const float BallRadius = 0.28f;
        public const float PaddleSpeed = 8.5f;
        public const float PaddleSpeedScaling = 0.65f;
        public const float BallStartSpeed = 7.25f;
        public const float BallAccelerationPerSecond = 0.34f;
        public const float CornerPaddleGap = 0.18f;
        public const float BallSimulationStepDistance = 0.12f;
        public const float ServeDelay = 3.0f;
        public const float ServeCountdownNumberDuration = 0.8f;
        public const float ServeCountdownStartDuration = 0.6f;
        public const float GameplayPopupDuration = 1.15f;
        public const float ScorePopupDuration = 1.45f;

        // Online simulation and presentation.
        // The authoritative simulation is fixed-rate so host FPS no longer changes game physics.
        public const uint NetcodeTickRate = 60;
        public const float ServerSimulationRate = 60f;
        public const int MaxServerCatchupSteps = 4;
        public const int MaxBallSimulationSubsteps = 32;

        // Fast motion is sent unreliably. Clients render a few snapshots behind server time
        // and adapt that delay to measured packet-arrival jitter.
        public const float MotionNetworkRate = 60f;
        public const float InputNetworkRate = 60f;
        public const int SnapshotHistorySize = 10;
        public const float SnapshotInterpolationBaseDelay = 0.040f;
        public const float SnapshotInterpolationMinDelay = 0.025f;
        public const float SnapshotInterpolationMaxDelay = 0.100f;
        public const float SnapshotJitterMultiplier = 2.0f;
        public const float SnapshotJitterSmoothing = 0.10f;
        public const float SnapshotDelayAdjustSpeed = 0.18f;
        public const float MaxBallSnapshotExtrapolation = 0.045f;

        // Local paddles are predicted immediately. The server receives the predicted
        // absolute position too and validates/corrects toward it, which keeps authority
        // close to what the player actually sees without visible tug-back.
        public const float MaxClientPaddlePositionCorrection = 1.35f;
        public const float LocalEmergencyCorrectionDistance = 3.25f;

        public const int InputStopRepeatCount = 3;
        public const float InputStopRepeatInterval = 0.020f;
        public const float InputStaleTimeout = 0.30f;

        public const int BrickColumns = 9;
        public const int BrickRows = 9;
        public const float BrickWidth = 0.64f;
        public const float BrickHeight = 0.38f;
        public const float BrickGap = 0.08f;
        public const float BrickDepth = 0.30f;
        public const float BrickPowerupChance = 0.20f;
        public const int MaxPowerupPickups = 32;
        public const float PowerupArmDelay = 0.80f;
        public const float PowerupLifetime = -1f;
        public const float PowerupPickupRadius = 0.34f;
        public const float ServeSpawnGap = 0.40f;

        public const int MaxBalls = 4;
        public const float AllyGrowAmount = 0.22f;
        public const float EnemyShrinkAmount = 0.18f;
        public const float MinPaddleScale = 0.55f;
        public const float MaxPaddleScale = 1.75f;

        public const int AllyPassesBeforeDebuff = 5;
        public const float AllyPassDebuffDuration = 8f;
        public const float AllyPassDebuffPaddleScale = 0.72f;

        public const ulong EmptyClient = ulong.MaxValue;
    }
}

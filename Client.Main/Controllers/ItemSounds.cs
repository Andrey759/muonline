namespace Client.Main.Controllers
{
    /// <summary>
    /// Interface sounds of item handling (original client files in Data/Sound): picking up and putting down an item in
    /// the inventory, vault or chaos machine, selling and buying.
    /// </summary>
    public static class ItemSounds
    {
        public const string Move = "Sound/pGetItem.wav";
        public const string Sell = "Sound/pDropMoney.wav";
        public const string Buy = "Sound/pGetItem.wav";

        public static void PlayMove() => SoundController.Instance.PlayBuffer(Move);
        public static void PlaySell() => SoundController.Instance.PlayBuffer(Sell);
        public static void PlayBuy() => SoundController.Instance.PlayBuffer(Buy);
    }
}

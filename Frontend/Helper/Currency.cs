namespace Frontend;

public readonly struct Currency
{
    public const int CopperPerSilver = 100;
    public const int CopperPerGold = 100 * CopperPerSilver;

    public static readonly Currency Empty = new(0);

    public int Gold { get; }
    public int Silver { get; }
    public int Copper { get; }

    public Currency(int amount)
    {
        int sign = amount < 0 ? -1 : 1;

        Gold = 0;
        Silver = 0;
        Copper = Math.Abs(amount);

        if (Copper >= CopperPerGold)
        {
            Gold = Copper / CopperPerGold * sign;
            Copper %= CopperPerGold;
        }

        if (Copper >= CopperPerSilver)
        {
            Silver = Copper / CopperPerSilver * sign;
            Copper %= CopperPerSilver;
        }

        Copper *= sign;
    }
}

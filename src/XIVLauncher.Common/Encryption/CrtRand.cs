namespace XIVLauncher.Common.Encryption;

public class CrtRand
{
    private uint seed;

    public CrtRand(uint seed) =>
        this.seed = seed;

    public uint Next()
    {
        seed = 0x343FD * seed + 0x269EC3;
        return seed >> 16 & 0xFFFF & 0x7FFF;
    }
}

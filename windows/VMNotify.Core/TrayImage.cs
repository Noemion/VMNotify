namespace VMNotify;

public sealed record TrayImage(int Width, int Height, string ArgbHex)
{
    public void Validate()
    {
        if (Width is < 1 or > 32 || Height is < 1 or > 32 || ArgbHex.Length != Width * Height * 8 || !ArgbHex.All(Uri.IsHexDigit))
            throw new InvalidDataException("无效的托盘图像数据。");
    }
    public byte[] BgraPixels()
    {
        Validate();
        var argb = Convert.FromHexString(ArgbHex);
        var bgra = new byte[argb.Length];
        for (int i = 0; i < argb.Length; i += 4) {
            bgra[i] = argb[i + 3]; bgra[i + 1] = argb[i + 2]; bgra[i + 2] = argb[i + 1]; bgra[i + 3] = argb[i];
        }
        return bgra;
    }
}

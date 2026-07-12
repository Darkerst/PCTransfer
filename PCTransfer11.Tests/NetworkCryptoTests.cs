using PCTransfer11.Services;
using Xunit;

namespace PCTransfer11.Tests;

public class NetworkCryptoTests
{
    [Fact]
    public void DeriveKey_IsDeterministic_ForSamePin()
    {
        byte[] key1 = NetworkCrypto.DeriveKey("123456");
        byte[] key2 = NetworkCrypto.DeriveKey("123456");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveKey_DiffersBetweenDifferentPins()
    {
        byte[] key1 = NetworkCrypto.DeriveKey("111111");
        byte[] key2 = NetworkCrypto.DeriveKey("222222");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveKey_Is32BytesLong_ForAes256()
    {
        Assert.Equal(32, NetworkCrypto.DeriveKey("000000").Length);
    }

    [Fact]
    public void GeneratePin_IsAlways6Digits()
    {
        for (int i = 0; i < 50; i++)
        {
            string pin = NetworkCrypto.GeneratePin();
            Assert.Equal(6, pin.Length);
            Assert.True(int.TryParse(pin, out _));
        }
    }
}

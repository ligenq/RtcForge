using RtcForge.Srtp;

namespace RtcForge.Tests.Srtp;

public class SrtpKeyDerivationTests
{
    [Fact]
    public void DeriveKey_SameInput_ProducesSameOutput()
    {
        // Arrange
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(42).NextBytes(masterSalt);

        // Act
        byte[] key1 = SrtpKeyDerivation.DeriveKey(masterKey, masterSalt, 0x00, 16);
        byte[] key2 = SrtpKeyDerivation.DeriveKey(masterKey, masterSalt, 0x00, 16);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveKey_DifferentLabels_ProduceDifferentOutputs()
    {
        // Arrange
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(42).NextBytes(masterSalt);

        // Act
        byte[] key1 = SrtpKeyDerivation.DeriveKey(masterKey, masterSalt, 0x00, 16);
        byte[] key2 = SrtpKeyDerivation.DeriveKey(masterKey, masterSalt, 0x01, 16);

        // Assert
        Assert.NotEqual(key1, key2);
    }
}

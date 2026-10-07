using Xunit;

namespace IssuerToken.Tests;

public class CpfTest
{
    [Theory]
    [InlineData("52998224725")]
    [InlineData("11144477735")]
    public void CpfValido(string cpf)
    {
        Assert.True(Cpf.EhValido(cpf));
    }

    [Theory]
    [InlineData("12345678900")] // dígitos verificadores errados
    [InlineData("11111111111")] // todos iguais
    [InlineData("5299822472")]  // 10 dígitos
    [InlineData("529982247250")] // 12 dígitos
    [InlineData("5299822472A")] // letra
    [InlineData("")]
    public void CpfInvalido(string cpf)
    {
        Assert.False(Cpf.EhValido(cpf));
    }

    [Theory]
    [InlineData("529.982.247-25", "52998224725")]
    [InlineData(" 52998224725 ", "52998224725")]
    [InlineData(null, "")]
    public void Normalizar_RemovePontuacao(string? entrada, string esperado)
    {
        Assert.Equal(esperado, Cpf.Normalizar(entrada));
    }
}

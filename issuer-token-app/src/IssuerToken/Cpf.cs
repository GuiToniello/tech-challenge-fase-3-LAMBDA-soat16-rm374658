namespace IssuerToken;

/// <summary>
/// Mesma regra do VO Cpf do repo APP: 11 dígitos, não todos iguais e dígitos verificadores válidos.
/// O valor normalizado (só dígitos) é o mesmo que o APP grava na coluna clientes.identificacao.
/// </summary>
public static class Cpf
{
    // Igual à normalização do APP: "529.982.247-25" -> "52998224725"
    public static string Normalizar(string? valor) =>
        new((valor ?? string.Empty).Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static bool EhValido(string cpf)
    {
        if (cpf.Length != 11 || !cpf.All(char.IsAsciiDigit) || cpf.Distinct().Count() == 1)
            return false;

        return cpf[9] - '0' == DigitoVerificador(cpf, 9)
            && cpf[10] - '0' == DigitoVerificador(cpf, 10);
    }

    // Módulo 11 sobre os primeiros "quantidade" dígitos, com pesos decrescentes até 2
    private static int DigitoVerificador(string cpf, int quantidade)
    {
        var soma = 0;
        for (var i = 0; i < quantidade; i++)
            soma += (cpf[i] - '0') * (quantidade + 1 - i);

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }
}

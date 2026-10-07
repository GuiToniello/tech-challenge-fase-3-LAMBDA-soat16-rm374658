using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Npgsql;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace IssuerToken;

public record BuscarClienteRequisicao([property: JsonPropertyName("cpf")] string Cpf);

public record BuscarClienteResposta([property: JsonPropertyName("existe")] bool Existe);

/// <summary>
/// Consulta no RDS se existe cliente com o CPF. Roda dentro da VPC (subnets privadas, sem acesso à
/// Internet) e é invocada pela EmitirTokenFunction, que fica fora da VPC para falar com o Auth0.
/// </summary>
public class BuscarClienteFunction
{
    // Mesmo mapeamento do repo APP: tabela clientes, coluna identificacao, tipo_identificacao 1 = CPF
    private const string Sql =
        "SELECT EXISTS (SELECT 1 FROM clientes WHERE identificacao = @cpf AND tipo_identificacao = 1)";

    // Pool de conexões reaproveitado entre as invocações do mesmo container
    private static readonly Lazy<NpgsqlDataSource> Banco = new(() => NpgsqlDataSource.Create(
        Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") is { Length: > 0 } connectionString
            ? connectionString
            : throw new InvalidOperationException("Variável de ambiente DB_CONNECTION_STRING não configurada.")));

    public async Task<BuscarClienteResposta> FunctionHandler(BuscarClienteRequisicao requisicao, ILambdaContext context)
    {
        await using var comando = Banco.Value.CreateCommand(Sql);
        comando.Parameters.AddWithValue("cpf", requisicao.Cpf);

        var existe = await comando.ExecuteScalarAsync() is true;

        context.Logger.LogInformation(existe ? "Cliente encontrado" : "Cliente não encontrado");
        return new BuscarClienteResposta(existe);
    }
}

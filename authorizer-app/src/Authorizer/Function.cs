using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Authorizer;

/// <summary>
/// Lambda authorizer do API Gateway (HTTP API do repo K8S, payload 2.0 com respostas simples).
/// Exige um JWT válido do Auth0 com a claim "cpf". As rotas de ROTAS_SEM_CPF (cadastro de cliente)
/// aceitam um JWT válido sem essa claim.
/// </summary>
public class Function
{
    private const string ClaimCpf = "cpf";

    // Criado uma vez por container: o cache do JWKS é reaproveitado entre as invocações
    private static readonly Lazy<ValidadorToken> ValidadorPadrao = new(ValidadorToken.CriarAPartirDoAmbiente);

    private readonly ValidadorToken _validador;
    private readonly HashSet<string> _rotasSemCpf;

    public Function() : this(ValidadorPadrao.Value, Environment.GetEnvironmentVariable("ROTAS_SEM_CPF")) { }

    /// <param name="rotasSemCpf">Rotas no formato "MÉTODO /path", separadas por vírgula.</param>
    public Function(ValidadorToken validador, string? rotasSemCpf)
    {
        _validador = validador;
        _rotasSemCpf = (rotasSemCpf ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizarRota)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<APIGatewayCustomAuthorizerV2SimpleResponse> FunctionHandler(
        APIGatewayCustomAuthorizerV2Request request, ILambdaContext context)
    {
        var rota = NormalizarRota($"{request.RequestContext?.Http?.Method} {request.RawPath}");

        var claims = await _validador.ValidarAsync(ExtrairToken(request.Headers));
        var cpf = claims is not null && claims.TryGetValue(ClaimCpf, out var valor) ? valor?.ToString() : null;

        // Token válido com cpf: qualquer rota. Token válido sem cpf: só as rotas isentas (cadastro de cliente)
        var autorizado = claims is not null && (!string.IsNullOrWhiteSpace(cpf) || _rotasSemCpf.Contains(rota));

        context.Logger.LogInformation($"{rota}: {(autorizado ? "autorizado" : "negado")}");

        return new APIGatewayCustomAuthorizerV2SimpleResponse
        {
            IsAuthorized = autorizado,
            Context = new Dictionary<string, object> { [ClaimCpf] = cpf ?? string.Empty }
        };
    }

    private static string? ExtrairToken(IDictionary<string, string>? headers)
    {
        const string prefixo = "Bearer ";

        var authorization = headers?
            .FirstOrDefault(header => header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            .Value;

        return authorization is not null && authorization.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefixo.Length..].Trim()
            : null;
    }

    // Comparação exata: só ignora espaços nas pontas e a barra final ("POST /monolith/api/clientes/")
    private static string NormalizarRota(string rota) => rota.Trim().TrimEnd('/');
}

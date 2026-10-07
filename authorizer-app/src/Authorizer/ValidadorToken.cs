using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Authorizer;

/// <summary>
/// Valida o JWT emitido pelo Auth0: assinatura (chaves do JWKS do tenant), issuer, audience,
/// expiração e algoritmo RS256.
/// </summary>
public class ValidadorToken
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuracao;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly JsonWebTokenHandler _handler = new();

    public ValidadorToken(IConfigurationManager<OpenIdConnectConfiguration> configuracao, string issuer, string audience)
    {
        _configuracao = configuracao;
        _issuer = issuer;
        _audience = audience;
    }

    /// <summary>Monta o validador com os dados do IdP vindos das variáveis de ambiente.</summary>
    public static ValidadorToken CriarAPartirDoAmbiente()
    {
        var issuer = $"https://{ObterVariavel("AUTH0_DOMAIN")}/";

        // Busca o discovery document e o JWKS do Auth0 e os mantém em cache
        var configuracao = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{issuer}.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

        return new ValidadorToken(configuracao, issuer, ObterVariavel("AUTH0_AUDIENCE"));
    }

    /// <returns>As claims do token, ou null quando o token está ausente ou é inválido.</returns>
    public async Task<IDictionary<string, object>?> ValidarAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var configuracao = await _configuracao.GetConfigurationAsync(CancellationToken.None);

        var resultado = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _issuer,
            ValidAudience = _audience,
            IssuerSigningKeys = configuracao.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        });

        return resultado.IsValid ? resultado.Claims : null;
    }

    private static string ObterVariavel(string nome) =>
        Environment.GetEnvironmentVariable(nome) is { Length: > 0 } valor
            ? valor
            : throw new InvalidOperationException($"Variável de ambiente {nome} não configurada.");
}

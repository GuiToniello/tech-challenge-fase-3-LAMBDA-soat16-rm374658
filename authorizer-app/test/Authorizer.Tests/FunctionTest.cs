using System.Security.Cryptography;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Authorizer.Tests;

// Os tokens são assinados com uma chave RSA local, publicada num JWKS fixo (sem acesso ao Auth0)
public class FunctionTest
{
    private const string Issuer = "https://tenant-teste.us.auth0.com/";
    private const string Audience = "http://localhost:7194";
    private const string CpfValido = "52998224725";

    private static readonly RsaSecurityKey ChaveDoTenant = new(RSA.Create(2048)) { KeyId = "chave-tenant" };
    private static readonly RsaSecurityKey OutraChave = new(RSA.Create(2048)) { KeyId = "outra-chave" };

    [Fact]
    public async Task TokenComCpf_Autoriza()
    {
        Assert.True(await Autorizar("GET", "/monolith/api/veiculos", Bearer(CriarToken(cpf: CpfValido))));
    }

    [Fact]
    public async Task TokenComCpf_RetornaCpfNoContexto()
    {
        var resposta = await CriarFunction().FunctionHandler(
            CriarRequest("GET", "/monolith/api/clientes", Bearer(CriarToken(cpf: CpfValido))), new TestLambdaContext());

        Assert.Equal(CpfValido, resposta.Context["cpf"]);
    }

    [Fact]
    public async Task TokenSemCpf_NegaRotaComum()
    {
        Assert.False(await Autorizar("GET", "/monolith/api/clientes", Bearer(CriarToken())));
    }

    [Theory]
    [InlineData("POST", "/monolith/api/clientes")]
    [InlineData("POST", "/monolith/api/clientes/")]
    [InlineData("post", "/Monolith/API/Clientes")]
    public async Task TokenSemCpf_AutorizaCadastroDeCliente(string metodo, string path)
    {
        Assert.True(await Autorizar(metodo, path, Bearer(CriarToken())));
    }

    [Theory]
    [InlineData("PUT", "/monolith/api/clientes")]
    [InlineData("POST", "/monolith/api/clientes/123")]
    [InlineData("POST", "/createos/api/ordens-servico/completa")]
    public async Task TokenSemCpf_NegaOutrasRotasParecidas(string metodo, string path)
    {
        Assert.False(await Autorizar(metodo, path, Bearer(CriarToken())));
    }

    [Fact]
    public async Task IssuerDiferente_Nega()
    {
        Assert.False(await Autorizar("GET", "/monolith/api/clientes", Bearer(CriarToken(cpf: CpfValido, issuer: "https://outro.auth0.com/"))));
    }

    [Fact]
    public async Task AudienceDiferente_Nega()
    {
        Assert.False(await Autorizar("GET", "/monolith/api/clientes", Bearer(CriarToken(cpf: CpfValido, audience: "https://outra-api"))));
    }

    [Fact]
    public async Task TokenExpirado_Nega()
    {
        Assert.False(await Autorizar("GET", "/monolith/api/clientes", Bearer(CriarToken(cpf: CpfValido, expiraEm: DateTime.UtcNow.AddHours(-1)))));
    }

    [Fact]
    public async Task AssinadoPorOutraChave_Nega()
    {
        Assert.False(await Autorizar("GET", "/monolith/api/clientes", Bearer(CriarToken(cpf: CpfValido, chave: OutraChave))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Bearer token-qualquer")]
    public async Task SemTokenValido_Nega(string? authorization)
    {
        Assert.False(await Autorizar("POST", "/monolith/api/clientes", authorization));
    }

    [Fact]
    public async Task EsquemaDiferenteDeBearer_Nega()
    {
        Assert.False(await Autorizar("GET", "/monolith/api/clientes", $"Basic {CriarToken(cpf: CpfValido)}"));
    }

    private static async Task<bool> Autorizar(string metodo, string path, string? authorization)
    {
        var resposta = await CriarFunction().FunctionHandler(CriarRequest(metodo, path, authorization), new TestLambdaContext());
        return resposta.IsAuthorized;
    }

    private static Function CriarFunction()
    {
        var jwks = new OpenIdConnectConfiguration { Issuer = Issuer };
        jwks.SigningKeys.Add(ChaveDoTenant);

        var validador = new ValidadorToken(new StaticConfigurationManager<OpenIdConnectConfiguration>(jwks), Issuer, Audience);
        return new Function(validador, "POST /monolith/api/clientes");
    }

    private static APIGatewayCustomAuthorizerV2Request CriarRequest(string metodo, string path, string? authorization) => new()
    {
        Type = "REQUEST",
        RawPath = path,
        Headers = authorization is null ? [] : new Dictionary<string, string> { ["authorization"] = authorization },
        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
        {
            Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = metodo, Path = path }
        }
    };

    private static string Bearer(string token) => $"Bearer {token}";

    private static string CriarToken(
        string? cpf = null,
        string issuer = Issuer,
        string audience = Audience,
        DateTime? expiraEm = null,
        SecurityKey? chave = null)
    {
        var expiracao = expiraEm ?? DateTime.UtcNow.AddMinutes(10);

        var claims = new Dictionary<string, object> { ["sub"] = "auth0|usuario-teste" };
        if (cpf is not null)
            claims["cpf"] = cpf;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = expiracao.AddMinutes(-20),
            NotBefore = expiracao.AddMinutes(-20),
            Expires = expiracao,
            SigningCredentials = new SigningCredentials(chave ?? ChaveDoTenant, SecurityAlgorithms.RsaSha256)
        });
    }
}

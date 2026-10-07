using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Xunit;

namespace IssuerToken.Tests;

public class EmitirTokenFunctionTest
{
    private const string CpfValido = "52998224725";
    private const string RespostaAuth0 = """{"access_token":"jwt-com-cpf","expires_in":86400,"token_type":"Bearer"}""";

    private static readonly Auth0Config Auth0 = new("tenant-teste.us.auth0.com", "http://localhost:7194", "client-id", "client-secret");

    [Fact]
    public async Task ClienteExistente_DevolveJwtDoAuth0()
    {
        var auth0 = new Auth0Falso(HttpStatusCode.OK, RespostaAuth0);

        var resposta = await Executar(auth0, Corpo("cliente@teste.com", "senha", "529.982.247-25"));

        Assert.Equal(200, resposta.StatusCode);
        Assert.Equal(RespostaAuth0, resposta.Body);
        Assert.Equal("https://tenant-teste.us.auth0.com/oauth/token", auth0.Url);
    }

    [Fact]
    public async Task ClienteExistente_PedeTokenAoAuth0ComCpfEAppSecret()
    {
        var auth0 = new Auth0Falso(HttpStatusCode.OK, RespostaAuth0);

        await Executar(auth0, Corpo("cliente@teste.com", "senha", "529.982.247-25"));

        Assert.Equal("password", auth0.Formulario["grant_type"]);
        Assert.Equal("client-id", auth0.Formulario["client_id"]);
        Assert.Equal("cliente@teste.com", auth0.Formulario["username"]);
        Assert.Equal("senha", auth0.Formulario["password"]);
        Assert.Equal("http://localhost:7194", auth0.Formulario["audience"]);
        Assert.Equal(CpfValido, auth0.Formulario["cpf"]);
        Assert.Equal("client-secret", auth0.Formulario["app_secret"]);
    }

    [Fact]
    public async Task CorpoEmBase64_EhAceito()
    {
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(Corpo("cliente@teste.com", "senha", CpfValido))),
            IsBase64Encoded = true
        };

        var resposta = await CriarFunction(new Auth0Falso(HttpStatusCode.OK, RespostaAuth0)).FunctionHandler(request, new TestLambdaContext());

        Assert.Equal(200, resposta.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-e-json")]
    [InlineData("""{"email":"cliente@teste.com","senha":"senha"}""")]
    [InlineData("""{"email":"","senha":"senha","cpf":"52998224725"}""")]
    public async Task CorpoInvalido_Devolve400(string? corpo)
    {
        var auth0 = new Auth0Falso(HttpStatusCode.OK, RespostaAuth0);

        var resposta = await Executar(auth0, corpo);

        Assert.Equal(400, resposta.StatusCode);
        Assert.Null(auth0.Url);
    }

    [Fact]
    public async Task CpfInvalido_Devolve400()
    {
        var resposta = await Executar(new Auth0Falso(HttpStatusCode.OK, RespostaAuth0), Corpo("cliente@teste.com", "senha", "12345678900"));

        Assert.Equal(400, resposta.StatusCode);
        Assert.Contains("CPF inválido", resposta.Body);
    }

    [Fact]
    public async Task ClienteNaoCadastrado_Devolve404SemChamarAuth0()
    {
        var auth0 = new Auth0Falso(HttpStatusCode.OK, RespostaAuth0);

        var resposta = await Executar(auth0, Corpo("cliente@teste.com", "senha", CpfValido), clienteExiste: false);

        Assert.Equal(404, resposta.StatusCode);
        Assert.Null(auth0.Url);
    }

    [Fact]
    public async Task FalhaNaBuscaDoCliente_Devolve503()
    {
        var function = new EmitirTokenFunction(
            _ => throw new InvalidOperationException("função VPC em Pending"),
            new HttpClient(new Auth0Falso(HttpStatusCode.OK, RespostaAuth0)),
            Auth0);

        var resposta = await function.FunctionHandler(
            new APIGatewayHttpApiV2ProxyRequest { Body = Corpo("cliente@teste.com", "senha", CpfValido) }, new TestLambdaContext());

        Assert.Equal(503, resposta.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task CredenciaisRecusadasPeloAuth0_Devolve401(HttpStatusCode statusAuth0)
    {
        var auth0 = new Auth0Falso(statusAuth0, """{"error":"invalid_grant","error_description":"Wrong email or password."}""");

        var resposta = await Executar(auth0, Corpo("cliente@teste.com", "senha-errada", CpfValido));

        Assert.Equal(401, resposta.StatusCode);
    }

    [Fact]
    public async Task ErroInesperadoDoAuth0_Devolve502()
    {
        var resposta = await Executar(new Auth0Falso(HttpStatusCode.InternalServerError, "{}"), Corpo("cliente@teste.com", "senha", CpfValido));

        Assert.Equal(502, resposta.StatusCode);
    }

    private static Task<APIGatewayHttpApiV2ProxyResponse> Executar(Auth0Falso auth0, string? corpo, bool clienteExiste = true) =>
        CriarFunction(auth0, clienteExiste).FunctionHandler(new APIGatewayHttpApiV2ProxyRequest { Body = corpo }, new TestLambdaContext());

    private static EmitirTokenFunction CriarFunction(Auth0Falso auth0, bool clienteExiste = true) =>
        new(_ => Task.FromResult(clienteExiste), new HttpClient(auth0), Auth0);

    private static string Corpo(string email, string senha, string cpf) =>
        JsonSerializer.Serialize(new { email, senha, cpf });

    // Responde no lugar do Auth0 e guarda o que a Lambda enviou
    private sealed class Auth0Falso(HttpStatusCode status, string corpo) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public Dictionary<string, string> Formulario { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();

            var conteudo = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var par in conteudo.Split('&'))
            {
                var partes = par.Split('=', 2);
                Formulario[WebUtility.UrlDecode(partes[0])] = WebUtility.UrlDecode(partes[1]);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        }
    }
}

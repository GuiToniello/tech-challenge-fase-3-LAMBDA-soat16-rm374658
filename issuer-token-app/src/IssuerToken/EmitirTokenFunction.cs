using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using InvokeRequest = Amazon.Lambda.Model.InvokeRequest;

namespace IssuerToken;

/// <summary>Dados do IdP, lidos das variáveis de ambiente.</summary>
public record Auth0Config(string Dominio, string Audience, string ClientId, string ClientSecret)
{
    public static Auth0Config DoAmbiente() => new(
        Ler("AUTH0_DOMAIN"), Ler("AUTH0_AUDIENCE"), Ler("AUTH0_CLIENT_ID"), Ler("AUTH0_CLIENT_SECRET"));

    private static string Ler(string nome) =>
        Environment.GetEnvironmentVariable(nome) is { Length: > 0 } valor
            ? valor
            : throw new InvalidOperationException($"Variável de ambiente {nome} não configurada.");
}

public record DadosLogin(string? Email, string? Senha, string? Cpf);

/// <summary>
/// POST /auth/token: recebe email, senha e CPF e devolve o JWT usado nos demais endpoints.
/// O token é emitido pelo Auth0 (grant password) já com a claim "cpf": a Action post-login
/// (auth0/post-login-cpf.js) grava a claim quando recebe o cpf e o app_secret enviados aqui.
/// </summary>
public class EmitirTokenFunction
{
    private static readonly HttpClient HttpPadrao = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Criado só na primeira invocação real (os testes injetam a busca e não precisam da AWS)
    private static readonly Lazy<AmazonLambdaClient> LambdaClient = new(() => new AmazonLambdaClient());

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Dictionary<string, string> CabecalhosJson = new() { ["Content-Type"] = "application/json" };

    private readonly Func<string, Task<bool>> _clienteExiste;
    private readonly HttpClient _http;
    private readonly Auth0Config _auth0;

    public EmitirTokenFunction() : this(InvocarBuscarCliente, HttpPadrao, Auth0Config.DoAmbiente()) { }

    public EmitirTokenFunction(Func<string, Task<bool>> clienteExiste, HttpClient http, Auth0Config auth0)
    {
        _clienteExiste = clienteExiste;
        _http = http;
        _auth0 = auth0;
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var login = LerCorpo(request);
        if (login is null
            || string.IsNullOrWhiteSpace(login.Email)
            || string.IsNullOrWhiteSpace(login.Senha)
            || string.IsNullOrWhiteSpace(login.Cpf))
        {
            return Erro(HttpStatusCode.BadRequest, "Informe email, senha e cpf.");
        }

        var cpf = Cpf.Normalizar(login.Cpf);
        if (!Cpf.EhValido(cpf))
            return Erro(HttpStatusCode.BadRequest, "CPF inválido.");

        bool clienteExiste;
        try
        {
            clienteExiste = await _clienteExiste(cpf);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Falha ao consultar o cliente: {ex.Message}");
            return Erro(HttpStatusCode.ServiceUnavailable, "Consulta de clientes indisponível. Tente novamente em instantes.");
        }

        if (!clienteExiste)
            return Erro(HttpStatusCode.NotFound, "Cliente não encontrado para o CPF informado.");

        return await EmitirNoAuth0(login.Email, login.Senha, cpf, context);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> EmitirNoAuth0(
        string email, string senha, string cpf, ILambdaContext context)
    {
        // Mesmo request de token das collections do Postman (grant password), mais cpf e app_secret,
        // que só a Action post-login lê
        using var formulario = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _auth0.ClientId,
            ["username"] = email,
            ["password"] = senha,
            ["audience"] = _auth0.Audience,
            ["cpf"] = cpf,
            ["app_secret"] = _auth0.ClientSecret
        });

        try
        {
            using var resposta = await _http.PostAsync($"https://{_auth0.Dominio}/oauth/token", formulario);
            var corpo = await resposta.Content.ReadAsStringAsync();

            if (resposta.IsSuccessStatusCode)
            {
                // { "access_token": "<JWT com a claim cpf>", "token_type": "Bearer", "expires_in": ... }
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = corpo,
                    Headers = CabecalhosJson
                };
            }

            if (resposta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Erro(HttpStatusCode.Unauthorized, "Credenciais inválidas.");

            context.Logger.LogError($"Auth0 respondeu {(int)resposta.StatusCode}: {corpo}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            context.Logger.LogError($"Falha ao chamar o Auth0: {ex.Message}");
        }

        return Erro(HttpStatusCode.BadGateway, "Falha ao emitir o token no Auth0.");
    }

    private static DadosLogin? LerCorpo(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return null;

        try
        {
            var json = request.IsBase64Encoded
                ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body))
                : request.Body;

            return JsonSerializer.Deserialize<DadosLogin>(json, Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static async Task<bool> InvocarBuscarCliente(string cpf)
    {
        var resposta = await LambdaClient.Value.InvokeAsync(new InvokeRequest
        {
            FunctionName = Environment.GetEnvironmentVariable("BUSCAR_CLIENTE_FUNCTION"),
            Payload = JsonSerializer.Serialize(new BuscarClienteRequisicao(cpf))
        });

        if (!string.IsNullOrEmpty(resposta.FunctionError))
            throw new InvalidOperationException($"A função de busca de clientes falhou ({resposta.FunctionError}).");

        var resultado = await JsonSerializer.DeserializeAsync<BuscarClienteResposta>(resposta.Payload);
        return resultado?.Existe ?? false;
    }

    private static APIGatewayHttpApiV2ProxyResponse Erro(HttpStatusCode status, string mensagem) => new()
    {
        StatusCode = (int)status,
        Body = JsonSerializer.Serialize(new { message = mensagem }, Json),
        Headers = CabecalhosJson
    };
}

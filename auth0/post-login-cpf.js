/**
 * Auth0 Action "Adicionar CPF ao token" (trigger Login / Post Login).
 *
 * A Lambda issue-token (POST /auth/token) consulta o cliente no banco e pede o token ao Auth0
 * (grant password) com dois parâmetros extras no corpo do /oauth/token:
 *   - cpf: CPF do cliente, já conferido no banco;
 *   - app_secret: Client Secret da application, que só a Lambda envia.
 *
 * A claim "cpf" só é gravada quando o app_secret confere com o secret CLIENT_SECRET desta Action.
 * Quem pede o token direto ao Auth0 (como o Postman no cadastro de cliente) recebe o token sem a claim.
 *
 * Secret da Action: CLIENT_SECRET = Client Secret da application (Applications > oficina-manager > Settings).
 */
exports.onExecutePostLogin = async (event, api) => {
  const body = event.request.body || {};

  if (body.cpf && body.app_secret === event.secrets.CLIENT_SECRET) {
    api.accessToken.setCustomClaim('cpf', body.cpf);
  }
};

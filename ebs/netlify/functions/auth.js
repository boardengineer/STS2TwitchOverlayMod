// Redirects the user to Twitch OAuth, embedding the game's local callback port in state.
exports.handler = async (event) => {
  const port = event.queryStringParameters?.port;
  if (!port) {
    return { statusCode: 400, body: 'Missing port parameter' };
  }

  const state = Buffer.from(JSON.stringify({ port })).toString('base64url');

  const params = new URLSearchParams({
    client_id:     process.env.CLIENT_ID,
    redirect_uri:  `${process.env.URL}/.netlify/functions/callback`,
    response_type: 'code',
    scope:         '',
    state,
  });

  return {
    statusCode: 302,
    headers: { Location: `https://id.twitch.tv/oauth2/authorize?${params}` },
  };
};

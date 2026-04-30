const crypto = require('crypto');
const jwt    = require('jsonwebtoken');

const JWT_LIFETIME_SECONDS = 900; // 15 minutes

exports.handler = async (event) => {
  const { code, state, error } = event.queryStringParameters ?? {};

  if (error) {
    return { statusCode: 400, body: `Twitch OAuth error: ${error}` };
  }
  if (!code || !state) {
    return { statusCode: 400, body: 'Missing code or state' };
  }

  let port;
  try {
    ({ port } = JSON.parse(Buffer.from(state, 'base64url').toString()));
  } catch {
    return { statusCode: 400, body: 'Invalid state parameter' };
  }

  // Exchange authorization code for user access token
  const tokenRes = await fetch('https://id.twitch.tv/oauth2/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      client_id:     process.env.EXTENSION_CLIENT_ID,
      client_secret: process.env.OAUTH_CLIENT_SECRET,
      code,
      grant_type:    'authorization_code',
      redirect_uri:  `${process.env.URL}/.netlify/functions/callback`,
    }),
  });

  if (!tokenRes.ok) {
    const body = await tokenRes.text();
    return { statusCode: 502, body: `Token exchange failed: ${body}` };
  }

  const { access_token: twitchToken } = await tokenRes.json();

  // Fetch the authenticated user's info
  const userRes = await fetch('https://api.twitch.tv/helix/users', {
    headers: {
      Authorization: `Bearer ${twitchToken}`,
      'Client-Id':   process.env.EXTENSION_CLIENT_ID,
    },
  });

  if (!userRes.ok) {
    return { statusCode: 502, body: 'Failed to fetch Twitch user info' };
  }

  const { data: [user] } = await userRes.json();
  const channelId = user.id;
  const ownerId   = user.id;
  const login     = user.login;

  const broadcastJwt   = createBroadcastJwt(channelId, ownerId);
  const refreshToken   = jwt.sign(
    { channel_id: channelId, owner_id: ownerId },
    process.env.JWT_REFRESH_SECRET,
    { expiresIn: '30d' }
  );

  const params = new URLSearchParams({
    jwt:           broadcastJwt,
    refresh_token: refreshToken,
    channel_id:    channelId,
    owner_id:      ownerId,
    login,
    expires_in:    String(JWT_LIFETIME_SECONDS),
  });

  return {
    statusCode: 302,
    headers: { Location: `http://localhost:${port}/callback?${params}` },
  };
};

function createBroadcastJwt(channelId, ownerId) {
  const secret = Buffer.from(process.env.EXTENSION_SECRET, 'base64');
  const now    = Math.floor(Date.now() / 1000);

  const header  = Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify({
    exp:          now + JWT_LIFETIME_SECONDS,
    user_id:      ownerId,
    role:         'external',
    channel_id:   channelId,
    pubsub_perms: { send: ['broadcast'] },
  })).toString('base64url');

  const signingInput = `${header}.${payload}`;
  const sig = crypto.createHmac('sha256', secret).update(signingInput).digest('base64url');

  return `${signingInput}.${sig}`;
}

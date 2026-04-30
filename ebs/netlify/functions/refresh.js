const crypto = require('crypto');
const jwt    = require('jsonwebtoken');

const JWT_LIFETIME_SECONDS = 900;

exports.handler = async (event) => {
  if (event.httpMethod !== 'POST') {
    return { statusCode: 405 };
  }

  let refreshToken;
  try {
    ({ refresh_token: refreshToken } = JSON.parse(event.body));
  } catch {
    return { statusCode: 400, body: 'Invalid JSON body' };
  }

  if (!refreshToken) {
    return { statusCode: 400, body: 'Missing refresh_token' };
  }

  let decoded;
  try {
    decoded = jwt.verify(refreshToken, process.env.CLIENT_SECRET);
  } catch (err) {
    return { statusCode: 401, body: `Invalid or expired refresh token: ${err.message}` };
  }

  const broadcastJwt = createBroadcastJwt(decoded.channel_id, decoded.owner_id);

  return {
    statusCode: 200,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ jwt: broadcastJwt, expires_in: JWT_LIFETIME_SECONDS }),
  };
};

function createBroadcastJwt(channelId, ownerId) {
  const secret = Buffer.from(process.env.TWITCH_EXTENSION_SECRET, 'base64');
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

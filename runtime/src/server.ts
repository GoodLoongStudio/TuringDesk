import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { DesktopIntentAgent } from './mock-agent.js'
import { ModelGatewayManager } from './model-gateway.js'
import type { RuntimeModelConfig } from './model-config.js'

const host = '127.0.0.1'
const port = Number(process.env.TURINGDESK_RUNTIME_PORT ?? '4317')
const desktopAgent = new DesktopIntentAgent()
const models = new ModelGatewayManager()

function json(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store'
  })
  res.end(JSON.stringify(body))
}

async function readJson(req: IncomingMessage): Promise<any> {
  const chunks: Buffer[] = []
  let bytes = 0
  for await (const chunk of req) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
    bytes += buffer.length
    if (bytes > 256 * 1024) throw new Error('Request too large')
    chunks.push(buffer)
  }
  return chunks.length === 0 ? {} : JSON.parse(Buffer.concat(chunks).toString('utf8'))
}

const server = createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && req.url === '/health') {
      json(res, 200, { ok: true, mode: models.mode, version: '0.2.0', model: models.current })
      return
    }

    if (req.method === 'GET' && req.url === '/v1/config/model') {
      json(res, 200, models.current)
      return
    }

    if (req.method === 'POST' && req.url === '/v1/config/model') {
      const body = await readJson(req) as RuntimeModelConfig
      const configured = await models.configure(body)
      json(res, 200, configured)
      return
    }

    if (req.method === 'POST' && req.url === '/v1/config/model/test') {
      const reply = await models.test()
      json(res, 200, { ok: true, reply })
      return
    }

    if (req.method === 'POST' && req.url === '/v1/chat') {
      const body = await readJson(req)
      if (typeof body.message !== 'string' || body.message.trim().length === 0) {
        json(res, 400, { error: 'message is required' })
        return
      }

      const message = body.message.trim()
      if (models.mode !== 'harness') {
        const nativeReply = await desktopAgent.tryRun(message)
        if (nativeReply) {
          json(res, 200, { reply: nativeReply })
          return
        }
      }

      const reply = await models.run(message)
      json(res, 200, { reply })
      return
    }

    json(res, 404, { error: 'not found' })
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    console.error('[runtime]', error)
    json(res, 500, { error: message })
  }
})

server.listen(port, host, () => {
  console.log(`[TuringDesk Runtime] http://${host}:${port} mode=${models.mode}`)
})

async function shutdown(): Promise<void> {
  await models.close().catch(() => undefined)
  server.close(() => process.exit(0))
}

process.on('SIGINT', () => void shutdown())
process.on('SIGTERM', () => void shutdown())

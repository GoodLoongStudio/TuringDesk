import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { createAgentGateway } from './harness-gateway.js'

const host = '127.0.0.1'
const port = Number(process.env.TURINGDESK_RUNTIME_PORT ?? '4317')
const gateway = createAgentGateway()

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
  if (chunks.length === 0) return {}
  return JSON.parse(Buffer.concat(chunks).toString('utf8'))
}

const server = createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && req.url === '/health') {
      json(res, 200, { ok: true, mode: gateway.mode, version: '0.1.0' })
      return
    }

    if (req.method === 'POST' && req.url === '/v1/chat') {
      const body = await readJson(req)
      if (typeof body.message !== 'string' || body.message.trim().length === 0) {
        json(res, 400, { error: 'message is required' })
        return
      }

      const reply = await gateway.run(body.message.trim())
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
  console.log(`[TuringDesk Runtime] http://${host}:${port} mode=${gateway.mode}`)
})

async function shutdown(): Promise<void> {
  await gateway.close?.()
  server.close(() => process.exit(0))
}

process.on('SIGINT', () => void shutdown())
process.on('SIGTERM', () => void shutdown())

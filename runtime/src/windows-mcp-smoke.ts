import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { resolve } from 'node:path'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { getDefaultEnvironment, StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js'

const calls: Array<{ name?: string; arguments?: Record<string, unknown> }> = []
const fakeDesktop = createServer(async (request, response) => {
  if (request.method !== 'POST' || request.url !== '/v1/capabilities/execute') {
    response.writeHead(404).end()
    return
  }

  const chunks: Buffer[] = []
  for await (const chunk of request) chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk))
  const body = JSON.parse(Buffer.concat(chunks).toString('utf8')) as { name?: string; arguments?: Record<string, unknown> }
  calls.push(body)

  const result = body.name === 'window.list'
    ? [{ handle: '42', title: 'Smoke Window', processId: 123, processName: 'smoke', x: 0, y: 0, width: 800, height: 600 }]
    : body.name === 'desktop.snapshot'
      ? {
          foregroundHandle: '42',
          monitors: [{ id: 'primary', left: 0, top: 0, width: 1920, height: 1080, isPrimary: true }],
          windows: [{ handle: '42', title: 'Smoke Window', processName: 'smoke' }]
        }
      : { accepted: true }

  response.writeHead(200, { 'content-type': 'application/json' })
  response.end(JSON.stringify({ ok: true, result, error: null }))
})

await new Promise<void>((resolveListen, rejectListen) => {
  fakeDesktop.once('error', rejectListen)
  fakeDesktop.listen(0, '127.0.0.1', () => resolveListen())
})

const address = fakeDesktop.address()
if (!address || typeof address === 'string') throw new Error('Fake desktop did not bind a TCP port')

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [resolve('dist/windows-mcp-server.js')],
  env: {
    ...getDefaultEnvironment(),
    TURINGDESK_CAPABILITY_URL: `http://127.0.0.1:${address.port}`
  },
  stderr: 'pipe'
})

const client = new Client({ name: 'turingdesk-mcp-smoke', version: '0.3.0' }, { capabilities: {} })

try {
  await client.connect(transport)
  const listed = await client.listTools()
  const names = listed.tools.map(tool => tool.name)

  for (const expected of ['desktop_snapshot', 'app_launch', 'window_list', 'window_find', 'window_focus', 'window_move', 'window_resize', 'window_tile']) {
    assert(names.includes(expected), `Missing MCP tool: ${expected}`)
  }

  const snapshot = await client.callTool({ name: 'desktop_snapshot', arguments: {} })
  assert.equal(snapshot.isError, undefined)
  assert.equal(calls.at(-1)?.name, 'desktop.snapshot')
  const snapshotContent = snapshot.content as Array<{ type?: string; text?: string }>
  assert(snapshotContent[0]?.text?.includes('foregroundHandle'))
  assert(snapshotContent[0]?.text?.includes('Smoke Window'))

  const result = await client.callTool({ name: 'window_list', arguments: {} })
  assert.equal(result.isError, undefined)
  assert.equal(calls.at(-1)?.name, 'window.list')

  const content = result.content as Array<{ type?: string; text?: string }>
  const first = content[0]
  assert(first && first.type === 'text' && typeof first.text === 'string' && first.text.includes('Smoke Window'))
  process.stdout.write('TuringDesk Windows MCP smoke passed.\n')
} finally {
  await client.close().catch(() => undefined)
  await new Promise<void>(resolveClose => fakeDesktop.close(() => resolveClose()))
}

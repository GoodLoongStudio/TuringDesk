import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { existsSync, mkdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const require = createRequire(import.meta.url)
const moduleDir = dirname(fileURLToPath(import.meta.url))

export interface HarnessWebState {
  ok: boolean
  url: string
  state: 'stopped' | 'starting' | 'ready' | 'failed'
  error?: string
}

export class HarnessWebSurface {
  readonly host = '127.0.0.1'
  readonly port = Number(process.env.TURINGDESK_HARNESS_WEB_PORT ?? '4319')
  readonly url = `http://${this.host}:${this.port}`

  private child: ChildProcessWithoutNullStreams | undefined
  private starting: Promise<string> | undefined
  private lastError: string | undefined

  async status(): Promise<HarnessWebState> {
    if (await this.isReady()) return { ok: true, url: this.url, state: 'ready' }
    if (this.starting) return { ok: true, url: this.url, state: 'starting' }
    if (this.lastError) return { ok: false, url: this.url, state: 'failed', error: this.lastError }
    return { ok: true, url: this.url, state: 'stopped' }
  }

  async ensureReady(): Promise<string> {
    if (await this.isReady()) return this.url
    if (!this.starting) {
      this.starting = this.start().finally(() => {
        this.starting = undefined
      })
    }
    return this.starting
  }

  async close(): Promise<void> {
    const child = this.child
    this.child = undefined
    if (!child || child.exitCode !== null) return
    child.kill()
  }

  private async start(): Promise<string> {
    this.lastError = undefined
    const binPath = resolveDshBin()
    const home = resolveHarnessHome()
    mkdirSync(home, { recursive: true })

    const child = spawn(process.execPath, [binPath, 'web', '--host', this.host, '--port', String(this.port)], {
      cwd: resolve(process.env.TURINGDESK_AGENT_CWD ?? process.cwd()),
      env: {
        ...process.env,
        DSH_HOME: home,
        DSH_CWD: resolve(process.env.TURINGDESK_AGENT_CWD ?? process.cwd()),
        TURINGDESK_MCP_NODE: process.execPath,
        TURINGDESK_MCP_SERVER: resolveWindowsMcpServer(),
        TURINGDESK_CAPABILITY_URL: process.env.TURINGDESK_CAPABILITY_URL ?? 'http://127.0.0.1:4318'
      },
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true
    }) as ChildProcessWithoutNullStreams

    this.child = child
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', (chunk: string) => process.stderr.write(`[harness-web] ${chunk}`))
    child.stderr.on('data', (chunk: string) => process.stderr.write(`[harness-web] ${chunk}`))
    child.on('error', (error) => {
      this.lastError = error.message
    })
    child.on('exit', (code, signal) => {
      if (this.child === child) this.child = undefined
      if (code !== 0 && code !== null) {
        this.lastError = `DeepSeek Harness WebUI exited (code=${code}, signal=${signal ?? 'none'})`
      }
    })

    try {
      await waitUntilReady(this.url, 45_000)
      this.lastError = undefined
      return this.url
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      this.lastError = message
      if (child.exitCode === null) child.kill()
      if (this.child === child) this.child = undefined
      throw error
    }
  }

  private async isReady(): Promise<boolean> {
    try {
      const response = await fetch(this.url, { signal: AbortSignal.timeout(750) })
      return response.ok
    } catch {
      return false
    }
  }
}

function resolveDshBin(): string {
  if (process.env.TURINGDESK_DSH_BIN) return resolve(process.env.TURINGDESK_DSH_BIN)
  const packagePath = require.resolve('@deepseek-ai/dsh/package.json')
  const candidate = join(dirname(packagePath), 'lib', 'bin.js')
  if (!existsSync(candidate)) throw new Error(`Bundled DeepSeek Harness CLI was not found: ${candidate}`)
  return candidate
}

function resolveWindowsMcpServer(): string {
  if (process.env.TURINGDESK_MCP_SERVER) return resolve(process.env.TURINGDESK_MCP_SERVER)
  const candidate = resolve(moduleDir, 'windows-mcp-server.js')
  if (!existsSync(candidate)) throw new Error(`Bundled TuringDesk MCP server was not found: ${candidate}`)
  return candidate
}

function resolveHarnessHome(): string {
  if (process.env.TURINGDESK_DSH_HOME) return resolve(process.env.TURINGDESK_DSH_HOME)
  if (process.platform === 'win32' && process.env.LOCALAPPDATA) {
    return join(process.env.LOCALAPPDATA, 'TuringDesk', 'DeepSeekHarness')
  }
  return resolve(process.cwd(), '.turingdesk-dsh')
}

async function waitUntilReady(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs
  let lastError = 'not ready'
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(1_500) })
      if (response.ok) return
      lastError = `HTTP ${response.status}`
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error)
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 300))
  }
  throw new Error(`DeepSeek Harness WebUI did not become ready at ${url}: ${lastError}`)
}

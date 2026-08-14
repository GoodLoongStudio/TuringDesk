import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { resolve } from 'node:path'
import { createBundledHarnessSpawnSpec } from './harness-runtime.js'

export interface AgentGateway {
  readonly mode: string
  ensureReady?(): Promise<void>
  run(message: string): Promise<string>
  close?(): Promise<void>
}

class MockGateway implements AgentGateway {
  readonly mode = 'mock'

  async ensureReady(): Promise<void> {}

  async run(message: string): Promise<string> {
    return `TuringDesk Mock received: “${message}”. Choose a model in Settings to run the full DeepSeek Harness agent kernel.`
  }
}

type JsonObject = Record<string, unknown>
type JsonRpcError = { code?: number; message?: string; data?: unknown }
type JsonRpcFrame = {
  jsonrpc?: string
  id?: number
  method?: string
  params?: unknown
  result?: unknown
  error?: JsonRpcError
}

type PendingRequest = {
  resolve(value: unknown): void
  reject(error: Error): void
  timer: ReturnType<typeof setTimeout>
}

type ActiveRun = {
  readonly sessionId: string
  readonly queued: JsonRpcFrame[]
  readonly events: unknown[]
  resolve(value: string): void
  reject(error: Error): void
  timer: ReturnType<typeof setTimeout>
  messageId?: string
  receivedReceipt: boolean
}

class DeepSeekHarnessGateway implements AgentGateway {
  readonly mode = 'harness'

  private child: ChildProcessWithoutNullStreams | undefined
  private stdoutBuffer = ''
  private nextRequestId = 1
  private readonly pending = new Map<number, PendingRequest>()
  private readonly sessionId = `turingdesk-${randomUUID().replaceAll('-', '')}`
  private initializePromise: Promise<void> | undefined
  private activeRun: ActiveRun | undefined
  private closing = false
  private restartTimer: ReturnType<typeof setTimeout> | undefined
  private restartAttempts = 0

  async ensureReady(): Promise<void> {
    await this.start()
  }

  async run(message: string): Promise<string> {
    await this.start()
    if (this.activeRun) throw new Error('TuringDesk supports one Harness turn at a time')

    return new Promise<string>((resolveRun, rejectRun) => {
      const run: ActiveRun = {
        sessionId: this.sessionId,
        queued: [],
        events: [],
        resolve: resolveRun,
        reject: rejectRun,
        receivedReceipt: false,
        timer: setTimeout(() => this.finishRun(run, new Error('Harness turn timed out')), 180_000)
      }
      this.activeRun = run

      void this.request('session/prompt', {
        sessionId: run.sessionId,
        contentBlocks: [{ type: 'text', text: message }]
      }, 30_000).then((result) => {
        if (this.activeRun !== run) return
        if (!isRecord(result) || typeof result.messageId !== 'string') {
          this.finishRun(run, new Error('Harness returned an invalid session/prompt result'))
          return
        }

        run.messageId = result.messageId
        for (const frame of run.queued.splice(0)) {
          this.processRunNotification(run, frame)
          if (this.activeRun !== run) break
        }
      }).catch((error: unknown) => {
        this.finishRun(run, asError(error))
      })
    })
  }

  async close(): Promise<void> {
    this.closing = true
    if (this.restartTimer) {
      clearTimeout(this.restartTimer)
      this.restartTimer = undefined
    }

    const child = this.child
    if (!child) return

    try {
      await this.request('shutdown', undefined, 2_000)
    } catch {
      // Best effort; process teardown below is authoritative.
    }

    child.stdin.end()
    if (child.exitCode === null) child.kill()
    this.child = undefined
    this.initializePromise = undefined
  }

  private start(): Promise<void> {
    if (this.restartTimer) {
      clearTimeout(this.restartTimer)
      this.restartTimer = undefined
    }
    this.closing = false

    if (!this.initializePromise) {
      this.initializePromise = this.initialize().catch((error) => {
        this.initializePromise = undefined
        throw error
      })
    }
    return this.initializePromise
  }

  private async initialize(): Promise<void> {
    this.ensureProcess()
    const maxTokens = positiveInteger(process.env.TURINGDESK_HARNESS_MAX_TOKENS, 8192)

    const result = await this.request('initialize', {
      cwd: resolve(process.env.TURINGDESK_AGENT_CWD ?? process.cwd()),
      provider: process.env.TURINGDESK_HARNESS_PROVIDER ?? 'deepseek-official',
      model: process.env.TURINGDESK_HARNESS_MODEL ?? 'deepseek-v4-flash',
      maxTokens
    }, 30_000)

    if (!isRecord(result)
      || !isRecord(result.serverInfo)
      || result.serverInfo.name !== 'deepseek-harness-sdk-runtime') {
      throw new Error('Bundled process did not identify itself as the DeepSeek Harness SDK runtime')
    }

    this.restartAttempts = 0
  }

  private ensureProcess(): ChildProcessWithoutNullStreams {
    if (this.child && this.child.exitCode === null) return this.child

    const spec = createBundledHarnessSpawnSpec()
    const child = spawn(spec.command, spec.args, {
      cwd: spec.cwd,
      env: spec.env,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true
    }) as ChildProcessWithoutNullStreams

    this.child = child
    this.stdoutBuffer = ''
    this.closing = false
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', (chunk: string) => this.onStdout(chunk))
    child.stderr.on('data', (chunk: string) => process.stderr.write(`[harness] ${chunk}`))
    child.on('error', (error) => this.failRuntime(error))
    child.on('exit', (code, signal) => {
      const reason = new Error(`Harness runtime exited (code=${code ?? 'null'}, signal=${signal ?? 'none'})`)
      this.child = undefined
      this.initializePromise = undefined
      if (!this.closing) {
        this.failRuntime(reason)
        this.scheduleRestart()
      }
    })

    process.stderr.write(`[harness] bundled runtime started with profile ${spec.profilePath}\n`)
    return child
  }

  private scheduleRestart(): void {
    if (this.closing || this.restartTimer || this.restartAttempts >= 5) return
    const delay = Math.min(1000 * (2 ** this.restartAttempts), 10_000)
    this.restartAttempts += 1
    process.stderr.write(`[harness] restart scheduled in ${delay}ms (attempt ${this.restartAttempts}/5)\n`)
    this.restartTimer = setTimeout(() => {
      this.restartTimer = undefined
      if (this.closing) return
      void this.start().catch((error: unknown) => {
        process.stderr.write(`[harness] restart failed: ${asError(error).message}\n`)
        this.scheduleRestart()
      })
    }, delay)
  }

  private request(method: string, params: unknown, timeoutMs: number): Promise<unknown> {
    const child = this.ensureProcess()
    const id = this.nextRequestId++
    const frame: JsonObject = { jsonrpc: '2.0', id, method }
    if (params !== undefined) frame.params = params

    return new Promise<unknown>((resolveRequest, rejectRequest) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        rejectRequest(new Error(`Harness request timed out: ${method}`))
      }, timeoutMs)

      this.pending.set(id, { resolve: resolveRequest, reject: rejectRequest, timer })
      child.stdin.write(`${JSON.stringify(frame)}\n`, (error) => {
        if (!error) return
        const pending = this.pending.get(id)
        if (!pending) return
        clearTimeout(pending.timer)
        this.pending.delete(id)
        pending.reject(error)
      })
    })
  }

  private onStdout(chunk: string): void {
    this.stdoutBuffer += chunk
    while (true) {
      const newline = this.stdoutBuffer.indexOf('\n')
      if (newline < 0) return

      const line = this.stdoutBuffer.slice(0, newline).trim()
      this.stdoutBuffer = this.stdoutBuffer.slice(newline + 1)
      if (!line) continue

      let frame: JsonRpcFrame
      try {
        frame = JSON.parse(line) as JsonRpcFrame
      } catch {
        process.stderr.write(`[harness] Ignoring malformed JSON-RPC line: ${line.slice(0, 240)}\n`)
        continue
      }

      this.onFrame(frame)
    }
  }

  private onFrame(frame: JsonRpcFrame): void {
    if (typeof frame.id === 'number' && !frame.method) {
      const pending = this.pending.get(frame.id)
      if (!pending) return
      clearTimeout(pending.timer)
      this.pending.delete(frame.id)

      if (frame.error) {
        const suffix = frame.error.code === undefined ? '' : ` (${frame.error.code})`
        pending.reject(new Error(`${frame.error.message ?? 'Harness JSON-RPC error'}${suffix}`))
      } else {
        pending.resolve(frame.result)
      }
      return
    }

    if (!frame.method) return
    const run = this.activeRun
    if (!run) return

    if (!run.messageId) {
      run.queued.push(frame)
      return
    }

    this.processRunNotification(run, frame)
  }

  private processRunNotification(run: ActiveRun, frame: JsonRpcFrame): void {
    if (this.activeRun !== run || !run.messageId) return

    if (!run.receivedReceipt) {
      if (frame.method === 'session.event'
        && isRecord(frame.params)
        && frame.params.sessionId === run.sessionId
        && isInboxReceipt(frame.params.event, run.messageId)) {
        run.receivedReceipt = true
        run.events.push(frame.params.event)
      }
      return
    }

    if (frame.method === 'session.event'
      && isRecord(frame.params)
      && frame.params.sessionId === run.sessionId) {
      run.events.push(frame.params.event)
      return
    }

    if (frame.method === 'session.status'
      && isRecord(frame.params)
      && frame.params.sessionId === run.sessionId
      && frame.params.status === 'idle') {
      this.finishRun(run, undefined, finalResponse(run.events))
    }
  }

  private finishRun(run: ActiveRun, error?: Error, value?: string): void {
    if (this.activeRun !== run) return
    clearTimeout(run.timer)
    this.activeRun = undefined
    if (error) run.reject(error)
    else run.resolve(value || '(Harness completed without a final text response.)')
  }

  private failRuntime(error: Error): void {
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timer)
      pending.reject(error)
      this.pending.delete(id)
    }
    if (this.activeRun) this.finishRun(this.activeRun, error)
  }
}

function isInboxReceipt(event: unknown, messageId: string): boolean {
  if (!isRecord(event) || event.type !== 'agent/inbox/spliced' || !isRecord(event.data)) return false
  const inserted = event.data.inserted
  return Array.isArray(inserted) && inserted.some((message) => isRecord(message) && message.id === messageId)
}

function finalResponse(events: unknown[]): string {
  for (let index = events.length - 1; index >= 0; index--) {
    const event = events[index]
    if (!isRecord(event) || event.type !== 'assistant/message' || !isRecord(event.data)) continue
    const message = event.data.message
    if (!isRecord(message) || !Array.isArray(message.content)) continue

    return message.content
      .filter((block) => isRecord(block) && block.type === 'text' && typeof block.text === 'string')
      .map((block) => (block as { text: string }).text)
      .join('')
  }
  return ''
}

function positiveInteger(value: string | undefined, fallback: number): number {
  if (!value) return fallback
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed <= 0) {
    throw new Error('TURINGDESK_HARNESS_MAX_TOKENS must be a positive integer')
  }
  return parsed
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function asError(value: unknown): Error {
  return value instanceof Error ? value : new Error(String(value))
}

export function createAgentGateway(): AgentGateway {
  return process.env.TURINGDESK_RUNTIME_MODE?.toLowerCase() === 'harness'
    ? new DeepSeekHarnessGateway()
    : new MockGateway()
}

export type AgentPhase = 'idle' | 'running' | 'completed' | 'error'

export interface AgentTraceItem {
  at: string
  kind: string
  text: string
}

export interface AgentRunSummary {
  id: number
  prompt: string
  phase: AgentPhase
  startedAt: string
  finishedAt?: string
  replyPreview?: string
  error?: string
  trace: AgentTraceItem[]
}

export interface AgentActivitySnapshot {
  phase: AgentPhase
  busy: boolean
  runId: number
  currentPrompt?: string
  startedAt?: string
  finishedAt?: string
  replyPreview?: string
  error?: string
  trace: AgentTraceItem[]
  history: AgentRunSummary[]
}

const HISTORY_LIMIT = 8
const TRACE_LIMIT = 28
const PREVIEW_LIMIT = 1000

export class AgentActivityTracker {
  private nextRunId = 1
  private phase: AgentPhase = 'idle'
  private current: AgentRunSummary | undefined
  private readonly history: AgentRunSummary[] = []

  begin(prompt: string): number {
    const run: AgentRunSummary = {
      id: this.nextRunId++,
      prompt: compact(prompt, 1600),
      phase: 'running',
      startedAt: new Date().toISOString(),
      trace: []
    }
    this.current = run
    this.phase = 'running'
    this.step(run.id, 'request', '请求已进入 TuringDesk Runtime')
    return run.id
  }

  step(runId: number, kind: string, text: string): void {
    if (!this.current || this.current.id !== runId) return
    const normalized = compact(text, 420)
    if (!normalized) return
    const previous = this.current.trace.at(-1)
    if (previous?.kind === kind && previous.text === normalized) return

    this.current.trace.push({
      at: new Date().toISOString(),
      kind: compact(kind || 'trace', 40),
      text: normalized
    })
    if (this.current.trace.length > TRACE_LIMIT) {
      this.current.trace.splice(0, this.current.trace.length - TRACE_LIMIT)
    }
  }

  complete(runId: number, reply: string): void {
    if (!this.current || this.current.id !== runId) return
    this.step(runId, 'complete', '执行完成，结果已返回桌面')
    this.current.phase = 'completed'
    this.current.finishedAt = new Date().toISOString()
    this.current.replyPreview = compact(reply, PREVIEW_LIMIT)
    this.phase = 'completed'
    this.pushHistory(this.current)
    this.current = undefined
  }

  fail(runId: number, error: unknown): void {
    if (!this.current || this.current.id !== runId) return
    const message = compact(error instanceof Error ? error.message : String(error), PREVIEW_LIMIT)
    this.step(runId, 'error', message)
    this.current.phase = 'error'
    this.current.finishedAt = new Date().toISOString()
    this.current.error = message
    this.phase = 'error'
    this.pushHistory(this.current)
    this.current = undefined
  }

  snapshot(): AgentActivitySnapshot {
    const latest = this.current ?? this.history[0]
    return {
      phase: this.current ? 'running' : this.phase,
      busy: Boolean(this.current),
      runId: latest?.id ?? 0,
      currentPrompt: this.current?.prompt,
      startedAt: latest?.startedAt,
      finishedAt: latest?.finishedAt,
      replyPreview: latest?.replyPreview,
      error: latest?.error,
      trace: latest?.trace.map((item) => ({ ...item })) ?? [],
      history: this.history.map((item) => ({
        ...item,
        trace: item.trace.map((trace) => ({ ...trace }))
      }))
    }
  }

  private pushHistory(run: AgentRunSummary): void {
    this.history.unshift({
      ...run,
      trace: run.trace.map((item) => ({ ...item }))
    })
    if (this.history.length > HISTORY_LIMIT) this.history.length = HISTORY_LIMIT
  }
}

function compact(value: string, limit: number): string {
  const normalized = value.replace(/\s+/g, ' ').trim()
  if (!normalized) return ''
  return normalized.length <= limit ? normalized : `${normalized.slice(0, limit - 1)}…`
}
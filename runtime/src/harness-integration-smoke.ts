import { spawn } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { createBundledHarnessSpawnSpec } from './harness-runtime.js'

type JsonRpcFrame = {
  id?: number
  result?: unknown
  error?: { message?: string }
}

type RecordValue = Record<string, unknown>

async function main(): Promise<void> {
  const tempRoot = mkdtempSync(join(tmpdir(), 'turingdesk-harness-smoke-'))
  process.env.TURINGDESK_RUNTIME_DATA_DIR = tempRoot
  process.env.TURINGDESK_AGENT_CWD = tempRoot
  process.env.TURINGDESK_HARNESS_PROVIDER = 'deepseek-official'
  process.env.TURINGDESK_HARNESS_MODEL = process.env.TURINGDESK_HARNESS_MODEL ?? 'deepseek-v4-flash'
  delete process.env.DEEPSEEK_API_KEY

  const spec = createBundledHarnessSpawnSpec()
  const child = spawn(spec.command, spec.args, {
    cwd: spec.cwd,
    env: spec.env,
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true
  })

  let stderr = ''
  let stdoutBuffer = ''
  let settled = false

  child.stderr.setEncoding('utf8')
  child.stdout.setEncoding('utf8')
  child.stderr.on('data', chunk => { stderr += String(chunk) })

  const initialized = new Promise<void>((resolveReady, rejectReady) => {
    const timeout = setTimeout(() => rejectReady(new Error(`Harness initialize timed out. stderr:\n${stderr}`)), 30_000)

    child.stdout.on('data', chunk => {
      stdoutBuffer += String(chunk)
      while (true) {
        const newline = stdoutBuffer.indexOf('\n')
        if (newline < 0) break
        const line = stdoutBuffer.slice(0, newline).trim()
        stdoutBuffer = stdoutBuffer.slice(newline + 1)
        if (!line) continue

        let frame: JsonRpcFrame
        try {
          frame = JSON.parse(line) as JsonRpcFrame
        } catch {
          clearTimeout(timeout)
          rejectReady(new Error(`Harness stdout was not pure JSON-RPC: ${line}\nstderr:\n${stderr}`))
          return
        }

        if (frame.id !== 1) continue
        clearTimeout(timeout)
        if (frame.error) {
          rejectReady(new Error(`Harness initialize failed: ${frame.error.message ?? 'unknown error'}\nstderr:\n${stderr}`))
          return
        }

        if (!isRecord(frame.result)
          || !isRecord(frame.result.serverInfo)
          || frame.result.serverInfo.name !== 'deepseek-harness-sdk-runtime') {
          rejectReady(new Error(`Unexpected Harness server identity: ${JSON.stringify(frame.result)}`))
          return
        }

        settled = true
        resolveReady()
      }
    })

    child.once('error', error => {
      clearTimeout(timeout)
      rejectReady(error)
    })
    child.once('exit', (code, signal) => {
      if (settled) return
      clearTimeout(timeout)
      rejectReady(new Error(`Harness exited before initialize (code=${code}, signal=${signal}). stderr:\n${stderr}`))
    })
  })

  child.stdin.write(`${JSON.stringify({
    jsonrpc: '2.0',
    id: 1,
    method: 'initialize',
    params: {
      cwd: resolve(tempRoot),
      provider: 'deepseek-official',
      model: process.env.TURINGDESK_HARNESS_MODEL,
      maxTokens: 1024
    }
  })}\n`)

  try {
    await initialized
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'shutdown' })}\n`)
    await waitForExit(child, 10_000)
    console.log(`Harness integration smoke passed: ${spec.binPath}`)
    console.log(`Profile: ${spec.profilePath}`)
    console.log(`MCP: ${spec.mcpServerPath}`)
  } finally {
    if (child.exitCode === null) child.kill()
    rmSync(tempRoot, { recursive: true, force: true })
  }
}

function waitForExit(child: ReturnType<typeof spawn>, timeoutMs: number): Promise<void> {
  if (child.exitCode !== null) return Promise.resolve()
  return new Promise((resolveExit, rejectExit) => {
    const timer = setTimeout(() => rejectExit(new Error('Harness did not exit after shutdown')), timeoutMs)
    child.once('exit', code => {
      clearTimeout(timer)
      if (code === 0) resolveExit()
      else rejectExit(new Error(`Harness shutdown exited with code ${code}`))
    })
  })
}

function isRecord(value: unknown): value is RecordValue {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

void main().catch(error => {
  console.error(error instanceof Error ? error.stack ?? error.message : String(error))
  process.exitCode = 1
})

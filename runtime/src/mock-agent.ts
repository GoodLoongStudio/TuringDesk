import { CapabilityClient, type WindowSummary } from './capability-client.js'

export class DesktopIntentAgent {
  constructor(private readonly capabilities = new CapabilityClient()) {}

  async tryRun(message: string): Promise<string | null> {
    const command = message.trim()
    const asksChrome = /chrome|谷歌浏览器|浏览器/i.test(command)
    const asksCode = /vs\s*code|vscode|visual\s+studio\s+code/i.test(command)
    const asksTerminal = /terminal|终端|powershell/i.test(command)
    const asksTile = /左右|并排|side\s*by\s*side|\btile\b/i.test(command)
    const asksWindowList = /窗口列表|列出窗口|list\s+windows|windows\s+list/i.test(command)

    if (asksChrome && asksCode && asksTile) {
      const [chromeLaunch, codeLaunch] = await Promise.all([
        this.capabilities.execute<{ app: string; launched: boolean }>('app.launch', { app: 'chrome' }),
        this.capabilities.execute<{ app: string; launched: boolean }>('app.launch', { app: 'code' })
      ])

      if (!chromeLaunch.launched || !codeLaunch.launched) {
        return `启动结果：Chrome=${chromeLaunch.launched ? '成功' : '失败'}，VS Code=${codeLaunch.launched ? '成功' : '失败'}。`
      }

      const [chrome, code] = await Promise.all([
        this.capabilities.waitForWindow('chrome'),
        this.capabilities.waitForWindow('visual studio code')
      ])

      if (!chrome || !code) return '应用已经启动，但暂时没有找到 Chrome 和 VS Code 的两个可管理顶层窗口。'

      const result = await this.capabilities.execute<{ tiled: boolean }>('window.tile', {
        leftHandle: chrome.handle,
        rightHandle: code.handle
      })

      return result.tiled
        ? '已完成：Chrome 在左侧，VS Code 在右侧。'
        : '找到了两个窗口，但 Windows 没有接受这次分屏操作。'
    }

    if (asksWindowList) {
      const windows = await this.capabilities.execute<WindowSummary[]>('window.list')
      if (windows.length === 0) return '当前没有发现可管理的顶层窗口。'

      return windows
        .slice(0, 12)
        .map(window => `${window.title} (${window.processName}, handle=${window.handle})`)
        .join('\n')
    }

    if (asksChrome) return this.launchSingle('chrome', 'Chrome')
    if (asksCode) return this.launchSingle('code', 'VS Code')
    if (asksTerminal) return this.launchSingle('terminal', 'Terminal')

    return null
  }

  private async launchSingle(app: string, label: string): Promise<string> {
    const result = await this.capabilities.execute<{ app: string; launched: boolean }>('app.launch', { app })
    return result.launched ? `已启动 ${label}。` : `未能启动 ${label}。`
  }
}

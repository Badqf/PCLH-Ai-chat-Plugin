''' <summary>
''' PCL Harness 插件：AI 助手（API v6）。
''' 在「启动」页右侧挂一张对话卡片，直接和 OpenAI 兼容接口聊天：流式输出、代码块识别、
''' 用量/上下文可视化，并能读启动器的游戏实例、直接启动某个版本、给下一次启动追加参数。
''' </summary>
Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

''' <summary>一条对话消息。</summary>
Public Class ChatMessage
    Public Role As String
    Public Content As String
End Class

Public Class AiChatPlugin
    Implements PCL.IPclPlugin
    Implements PCL.IPclPluginUi
    Implements PCL.IPclPluginUi2

    ' JSON / 转义用到的字符（放常量里，避免到处写字面量）
    Private Shared ReadOnly ChBack As Char = Convert.ToChar(92)    ' \
    Private Shared ReadOnly ChQuote As Char = Convert.ToChar(34)   ' "
    Private Shared ReadOnly ChN As Char = Convert.ToChar(110)      ' n
    Private Shared ReadOnly ChT As Char = Convert.ToChar(116)      ' t
    Private Shared ReadOnly ChR As Char = Convert.ToChar(114)      ' r
    Private Shared ReadOnly ChB As Char = Convert.ToChar(98)       ' b
    Private Shared ReadOnly ChF As Char = Convert.ToChar(102)      ' f
    Private Shared ReadOnly ChU As Char = Convert.ToChar(117)      ' u

    ''' <summary>代码块围栏。</summary>
    Private Const Fence As String = "```"

    ' ==================== 状态 ====================

    Private Host As PCL.PluginHost
    Private ReadOnly Messages As New List(Of ChatMessage)
    Private ReadOnly SyncRoot As New Object()

    Private BaseUrl As String = "https://api.deepseek.com"
    Private ApiKey As String = ""
    Private ModelName As String = "deepseek-chat"
    Private CtxLimit As Integer = 64000
    Private SysPrompt As String = ""

    Private Const PromptVer As Integer = 6

    Private Function DefaultSysPrompt() As String
        Return "你是 PCL Harness 启动器里的助手。回答简洁、直接，用中文；不确定就直说不确定，不要编。" & Environment.NewLine & Environment.NewLine &
            "你能调用工具真的操作启动器：列实例、切换当前实例、启动游戏、看当前实例信息、追加 JVM/游戏参数、列插件、开关插件、列 mods/资源包/光影、启用或禁用它们、读游戏日志、读全局设置、写笔记、打开链接。" & Environment.NewLine &
            "规则：" & Environment.NewLine &
            "1. 涉及版本列表、启动游戏、改动参数、插件状态、模组状态、日志内容时，必须真的调用对应工具，不能凭上下文猜；" & Environment.NewLine &
            "2. 只有看到工具返回之后，才能说已启动/已追加/已切换/当前是这类话；工具没返回就别说做了；" & Environment.NewLine &
            "3. 用户问有哪些版本、现在挂了什么参数时，先调 list_instances / get_pending_args 再答，不要凭记忆；" & Environment.NewLine &
            "4. 工具报错就把原文告诉用户，不要包装成成功；" & Environment.NewLine &
            "5. 严禁编造工具输出：不得虚构日志行、目录内容、文件列表、错误文案或统计数字。你关于启动器现状的每一句具体内容，都必须来自本轮工具返回的原文；" & Environment.NewLine &
            "6. 如果还没调用工具，就不要说已经读到了或查到了，只能先调用工具，或直接告诉用户你需要先查；" & Environment.NewLine &
            "7. 引用工具返回时逐字复制关键行（尤其是报错原文），不要改写、不要补全、不要用示例代替。" & Environment.NewLine &
            Environment.NewLine &
            "能力边界（很重要）：" & Environment.NewLine &
            "· 你没有任何执行代码或命令行的手段，也不能读写任意路径的文件；" & Environment.NewLine &
            "· 唯一能写的地方是本插件 notes 目录（write_note），只能写 .md/.txt，不能碰启动器或插件的内部文件；" & Environment.NewLine &
            "· 改动类操作（启动游戏、切实例、追加参数、开关插件、启用禁用模组）只在用户明确要求时做，做完把工具原文回给用户；" & Environment.NewLine &
            "· 开关插件会让启动器重载插件、可能打断本次对话，操作前先说清后果；不能操作 aichat 自己；" & Environment.NewLine &
            "· 启用/禁用模组只是把文件名改成 .disabled，可逆；不要改动文件内容；" & Environment.NewLine &
            "· 权限是 API v7 显式声明的：没有声明的能力会直接抛 UnauthorizedAccessException，遇到就如实转述；" & Environment.NewLine &
            "· 用户要求做这些以外的事（改配置文件、删文件、跑脚本、装新软件），直接说做不到，并给出用户自己操作的步骤。"
    End Function

    Private Client As HttpClient
    Private Cts As CancellationTokenSource
    Private Busy As Boolean
    Private CurTarget As ChatMessage
    Private CurBubble As StackPanel
    Private ReadOnly Pending As New StringBuilder()
    Private LastFlush As DateTime = DateTime.MinValue

    ' 用量
    Private LastPromptToks As Integer = 0
    Private LastCompletionToks As Integer = 0

    ' ==================== UI 引用 ====================

    Private MsgPanel As StackPanel
    Private Scroller As PCL.MyScrollViewer
    Private InputBox As TextBox
    Private SendBtn As PCL.MyButton
    Private StopBtn As PCL.MyButton
    Private StatusText As TextBlock
    Private UsageText As TextBlock
    Private CtxGrid As Grid
    Private ConfPanel As StackPanel
    Private CfgBase As TextBox
    Private CfgKey As TextBox
    Private CfgModel As TextBox
    ' 工具调用：本轮请求里已收到的 tool_calls（按 index 累积参数片段）
    Private ToolCalls As New List(Of ToolCallReq)
    Private Const MaxToolRounds As Integer = 4
    ' 上一轮实际执行了多少次工具（状态行展示，用于识别“没查就编”）
    Private LastToolCount As Integer = 0

    ''' <summary>粗略判断这句话是不是在要求操作启动器。是的话强制模型第一轮必须调工具。</summary>
    Private Function LooksLikeAction(T As String) As Boolean
        If String.IsNullOrWhiteSpace(T) Then Return False
        Dim Ks() As String = {"读", "日志", "列", "查", "看", "启动", "开", "跑", "追加", "加", "改", "设", "写", "记", "保存", "打开",
                              "插件", "实例", "版本", "mod", "参数", "jvm", "cpu", "内存", "启动器", "链接", "网址", "记录"}
        For Each K In Ks
            If T.IndexOf(K, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
        Next
        Return False
    End Function

    Private Class ToolCallReq
        Public Id As String
        Public Name As String
        Public Args As New StringBuilder()
    End Class

    ' ==================== 生命周期 ====================

    Public Sub OnLoad(Host As PCL.PluginHost) Implements PCL.IPclPlugin.OnLoad
        Me.Host = Host
        LoadSettings()
        RebuildClient()
        LoadHistory()
        Host.LogInfo($"AI 助手已加载：模型 {ModelName}，接口 {BaseUrl}，历史 {Messages.Count} 条")
    End Sub

    Public Sub OnUnload() Implements PCL.IPclPlugin.OnUnload
        Try
            If Cts IsNot Nothing Then Cts.Cancel()
        Catch ex As Exception
        End Try
        SaveHistory()
        If Host IsNot Nothing Then Host.LogInfo("AI 助手已卸载")
    End Sub

    ' ==================== 配置 ====================

    Private Function GetStr(Key As String, Def As String) As String
        Try
            Dim V = Host.GetSetting(Key, CObj(Def))
            If V Is Nothing Then Return Def
            Dim S = V.ToString()
            If String.IsNullOrWhiteSpace(S) Then Return Def
            Return S
        Catch ex As Exception
            Return Def
        End Try
    End Function

    Private Function GetInt(Key As String, Def As Integer) As Integer
        Try
            Dim V = Host.GetSetting(Key, CObj(Def))
            If V Is Nothing Then Return Def
            Dim N As Integer
            If Integer.TryParse(V.ToString(), N) Then
                If N > 0 Then Return N
            End If
            Return Def
        Catch ex As Exception
            Return Def
        End Try
    End Function

    Private Sub LoadSettings()
        BaseUrl = GetStr("apiBase", "https://api.deepseek.com")
        ApiKey = GetStr("apiKey", "")
        ModelName = GetStr("model", "deepseek-chat")
        CtxLimit = GetInt("contextLimit", 64000)
        ' 提示词是随版本升级的：老设置里的旧 prompt 会盖掉新默认值，所以按 promptVer 强制刷新
        If GetInt("promptVer", 0) < PromptVer Then
            SysPrompt = DefaultSysPrompt()
            Try
                Host.SetSetting("systemPrompt", SysPrompt)
                Host.SetSetting("promptVer", PromptVer)
            Catch ex As Exception
            End Try
        Else
            SysPrompt = GetStr("systemPrompt", DefaultSysPrompt())
        End If
    End Sub

    Private Sub SaveSettings()
        Try
            Host.SetSetting("apiBase", BaseUrl)
            Host.SetSetting("apiKey", ApiKey)
            Host.SetSetting("model", ModelName)
            Host.SetSetting("systemPrompt", SysPrompt)
            Host.SetSetting("contextLimit", CtxLimit)
            RebuildClient()
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("保存设置失败：" & ex.Message)
        End Try
    End Sub

    ''' <summary>按当前端点重建 HttpClient：本地地址不走系统代理（避免代理把 127.0.0.1 劫走）。</summary>
    Private Sub RebuildClient()
        Try
            If Client IsNot Nothing Then Client.Dispose()
        Catch ex As Exception
        End Try
        Dim Handler As New HttpClientHandler()
        If IsLocalEndpoint() Then
            Handler.UseProxy = False
            Handler.Proxy = Nothing
        End If
        Client = New HttpClient(Handler)
        Client.Timeout = Timeout.InfiniteTimeSpan
    End Sub

    ''' <summary>把「连不上」的异常翻成人话，带上端点和该查什么。</summary>
    Private Function ConnectErrorText(Ex As Exception) As String
        Dim Ep As String = ApiEndpoint()
        Dim Inner As String = If(Ex.InnerException IsNot Nothing, Ex.InnerException.Message, Ex.Message)
        Dim Hint As String
        If IsLocalEndpoint() Then
            Hint = "本地服务没在监听：先启动 Ollama（托盘图标，或命令行 ollama serve），再用 ollama list 确认有 " & ModelName & "。 "
        Else
            Hint = "检查接口地址 / 网络 / 代理。 "
        End If
        Return $"连不上 {Ep}：{Inner}。{Hint}"
    End Function
    Private Function IsLocalEndpoint() As Boolean
        Dim B = If(BaseUrl, "").ToLowerInvariant()
        Return B.Contains("127.0.0.1") OrElse B.Contains("localhost")
    End Function

    Private Function ApiEndpoint() As String
        Dim B = If(BaseUrl, "").Trim().TrimEnd(Convert.ToChar("/"))
        If B.Length = 0 Then B = "https://api.deepseek.com"
        If B.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) Then Return B
        Return B & "/chat/completions"
    End Function

    ' ==================== 历史读写 ====================

    Private Function HistoryPath() As String
        If Host Is Nothing Then Return Nothing
        Return Path.Combine(Host.DataFolder, "chat.txt")
    End Function

    ''' <summary>每行「role TAB 内容」，内容用 base64 存（正文里可能有换行/制表/反斜杠，分行存会被撕碎）。</summary>
    Private Sub LoadHistory()
        Messages.Clear()
        Try
            Dim P = HistoryPath()
            If P Is Nothing OrElse Not File.Exists(P) Then Return
            For Each Line In File.ReadAllLines(P, Encoding.UTF8)
                If String.IsNullOrWhiteSpace(Line) Then Continue For
                Dim Tab = Line.IndexOf(Convert.ToChar(9))
                If Tab <= 0 Then Continue For
                Dim Role = Line.Substring(0, Tab)
                Dim Body = DecodeBody(Line.Substring(Tab + 1))
                If Body Is Nothing Then Continue For
                ' 旧格式把代码块拆成多行，会留下只有围栏的残片（如「```python」），丢掉。
                Dim Trimmed = Body.Trim()
                If Trimmed.StartsWith("```") AndAlso Trimmed.IndexOf("```", 3) < 0 Then Continue For
                Messages.Add(New ChatMessage With {.Role = Role, .Content = Body})
            Next
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("读取历史失败：" & ex.Message)
        End Try
        ' 读的时候丢掉过旧格式残片，顺手按新格式重写一遍。
        If Messages.Count > 0 Then SaveHistory()
    End Sub

    Private Function EncodeBody(B As String) As String
        Return "b64:" & Convert.ToBase64String(Encoding.UTF8.GetBytes(B))
    End Function

    ''' <summary>新格式是 b64: 前缀；老格式（\n 转义 + 物理分行）还能读，读不了就返回 Nothing 丢掉残片。</summary>
    Private Function DecodeBody(Raw As String) As String
        If Raw.StartsWith("b64:") Then
            Try
                Return Encoding.UTF8.GetString(Convert.FromBase64String(Raw.Substring(4)))
            Catch ex As Exception
                Return Nothing
            End Try
        End If
        Return Raw.Replace("\n", Environment.NewLine)
    End Function

    Private Sub SaveHistory()
        Try
            Dim P = HistoryPath()
            If P Is Nothing Then Return
            Dim Dir = Path.GetDirectoryName(P)
            If Not Directory.Exists(Dir) Then Directory.CreateDirectory(Dir)
            Dim Sb As New StringBuilder()
            For Each M In Messages
                If String.IsNullOrWhiteSpace(M.Content) Then Continue For
                Sb.Append(M.Role).Append(Convert.ToChar(9))
                Sb.Append(EncodeBody(M.Content))
                Sb.Append(Environment.NewLine)
            Next
            File.WriteAllText(P, Sb.ToString(), New UTF8Encoding(False))
        Catch ex As Exception
            If Host IsNot Nothing Then Host.LogWarn("保存历史失败：" & ex.Message)
        End Try
    End Sub

    ' ==================== 接口实现 ====================

    Public Function GetPages() As IEnumerable(Of PCL.PluginPageInfo) Implements PCL.IPclPluginUi.GetPages
        Return Nothing
    End Function

    Public Function GetNavItems() As IEnumerable(Of PCL.PluginNavItem) Implements PCL.IPclPluginUi.GetNavItems
        Return Nothing
    End Function

    Public Function GetSettings() As IEnumerable(Of PCL.PluginSetting) Implements PCL.IPclPluginUi.GetSettings
        Return New List(Of PCL.PluginSetting) From {
            New PCL.PluginSetting With {
                .Key = "apiBase",
                .Title = "接口地址",
                .Description = "OpenAI 兼容的 base，例如 https://api.deepseek.com、https://api.openai.com/v1，" &
                               "或本地 Ollama 的 http://127.0.0.1:11434/v1（本地不需要密钥）。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = "https://api.deepseek.com"
            },
            New PCL.PluginSetting With {
                .Key = "apiKey",
                .Title = "API Key",
                .Description = "留空则只能连本机（127.0.0.1 / localhost）的接口。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = ""
            },
            New PCL.PluginSetting With {
                .Key = "model",
                .Title = "模型",
                .Description = "例如 deepseek-chat、gpt-4o-mini、qwen2.5。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = "deepseek-chat"
            },
            New PCL.PluginSetting With {
                .Key = "systemPrompt",
                .Title = "预设提示词",
                .Description = "每次对话开头附带的 system 消息。想让 AI 帮你分析启动器状态，可以在这里写规则。",
                .SettingType = PCL.PluginSettingType.Text,
                .DefaultValue = "你是 PCL Harness 启动器里的助手。回答简洁、直接，用中文；不确定就直说不确定，不要编。"
            },
            New PCL.PluginSetting With {
                .Key = "contextLimit",
                .Title = "上下文上限（token）",
                .Description = "只用来画用量条。deepseek-chat 是 64000，Ollama 的 qwen2.5 一般是 32000。",
                .SettingType = PCL.PluginSettingType.Number,
                .DefaultValue = 64000,
                .MinValue = 1000,
                .MaxValue = 1000000
            }
        }
    End Function

    Public Function GetHomeCards() As IEnumerable(Of PCL.PluginCardInfo) Implements PCL.IPclPluginUi2.GetHomeCards
        Dim Card As New PCL.PluginCardInfo With {
            .Id = "aichat.home.card",
            .Title = "AI 助手",
            .Description = "对话 · 实例 · 启动",
            .Factory = Function() BuildCard()
        }
        Return New List(Of PCL.PluginCardInfo) From {Card}
    End Function

    Public Function GetSettingsCards() As IEnumerable(Of PCL.PluginCardInfo) Implements PCL.IPclPluginUi2.GetSettingsCards
        Return Nothing
    End Function

    Public Function GetStatusWidgets() As IEnumerable(Of PCL.PluginStatusWidget) Implements PCL.IPclPluginUi2.GetStatusWidgets
        ' 3.6.4 起宿主不再渲染状态栏组件
        Return Nothing
    End Function

    ' ==================== 卡片 UI ====================

    Private Function BuildCard() As Object
        LoadSettings()

        Dim Root As New StackPanel With {.Margin = New Thickness(0, 2, 0, 0)}

        ' ---- 顶部状态行 ----
        Dim TopRow As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .Margin = New Thickness(0, 0, 0, 4)
        }
        Dim StatusCol As New StackPanel With {.Width = 238, .Margin = New Thickness(0, 0, 8, 0)}
        StatusText = New TextBlock With {
            .FontSize = 11,
            .TextWrapping = TextWrapping.Wrap
        }
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        StatusCol.Children.Add(StatusText)

        UsageText = New TextBlock With {
            .FontSize = 10,
            .Margin = New Thickness(0, 1, 0, 0),
            .TextWrapping = TextWrapping.Wrap
        }
        UsageText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        StatusCol.Children.Add(UsageText)
        TopRow.Children.Add(StatusCol)

        Dim CfgBtn As New PCL.MyButton With {.Text = "配置", .Height = 24, .MinWidth = 48, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler CfgBtn.Click,
            Sub(sender, e)
                If ConfPanel IsNot Nothing Then
                    ConfPanel.Visibility = If(ConfPanel.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
                End If
            End Sub
        TopRow.Children.Add(CfgBtn)

        Dim ClearBtn As New PCL.MyButton With {.Text = "清空", .Height = 24, .MinWidth = 48}
        AddHandler ClearBtn.Click,
            Sub(sender, e)
                If Busy Then
                    Host.Hint("正在回答，先点「停止」")
                    Return
                End If
                Messages.Clear()
                SaveHistory()
                RenderAll()
                UpdateStatus()
            End Sub
        TopRow.Children.Add(ClearBtn)
        Root.Children.Add(TopRow)

        ' ---- 上下文用量条 ----
        CtxGrid = New Grid With {.Height = 3, .Margin = New Thickness(0, 0, 0, 8)}
        CtxGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(0, GridUnitType.Star)})
        CtxGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Dim CtxBg As New Border With {
            .CornerRadius = New CornerRadius(2),
            .Background = New SolidColorBrush(Color.FromArgb(26, 255, 255, 255))
        }
        Grid.SetColumnSpan(CtxBg, 2)
        CtxGrid.Children.Add(CtxBg)
        Dim CtxFill As New Border With {
            .CornerRadius = New CornerRadius(2),
            .HorizontalAlignment = HorizontalAlignment.Stretch
        }
        CtxFill.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        Grid.SetColumn(CtxFill, 0)
        CtxGrid.Children.Add(CtxFill)
        Root.Children.Add(CtxGrid)

        ' ---- 配置面板 ----
        ConfPanel = New StackPanel With {.Visibility = Visibility.Collapsed, .Margin = New Thickness(0, 0, 0, 8)}
        CfgBase = New TextBox()
        CfgKey = New TextBox()
        CfgModel = New TextBox()
        ConfPanel.Children.Add(MakeFieldRow("接口", CfgBase, BaseUrl))
        ConfPanel.Children.Add(MakeFieldRow("密钥", CfgKey, ApiKey))
        ConfPanel.Children.Add(MakeFieldRow("模型", CfgModel, ModelName))

        Dim QuickRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 2, 0, 6)}
        Dim DeepSeekBtn As New PCL.MyButton With {.Text = "用 DeepSeek", .Height = 24, .MinWidth = 96, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler DeepSeekBtn.Click,
            Sub(sender, e)
                CfgBase.Text = "https://api.deepseek.com"
                CfgModel.Text = "deepseek-chat"
            End Sub
        QuickRow.Children.Add(DeepSeekBtn)

        Dim OllamaBtn As New PCL.MyButton With {.Text = "用本地 Ollama", .Height = 24, .MinWidth = 104, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler OllamaBtn.Click,
            Sub(sender, e)
                CfgBase.Text = "http://127.0.0.1:11434/v1"
                CfgModel.Text = "qwen2.5"
                CfgKey.Text = ""
            End Sub
        QuickRow.Children.Add(OllamaBtn)

        Dim SaveBtn As New PCL.MyButton With {.Text = "保存", .Height = 24, .MinWidth = 56}
        AddHandler SaveBtn.Click,
            Sub(sender, e)
                BaseUrl = If(CfgBase.Text, "").Trim()
                ApiKey = If(CfgKey.Text, "").Trim()
                ModelName = If(CfgModel.Text, "").Trim()
                If ModelName.Length = 0 Then ModelName = "deepseek-chat"
                SaveSettings()
                UpdateStatus()
                Host.Hint("AI 助手配置已保存")
            End Sub
        QuickRow.Children.Add(SaveBtn)
        ConfPanel.Children.Add(QuickRow)
        Root.Children.Add(ConfPanel)

        ' ---- 对话区 ----
        MsgPanel = New StackPanel()
        Scroller = New PCL.MyScrollViewer With {
            .Height = 258,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Padding = New Thickness(0, 0, 4, 0),
            .Content = MsgPanel
        }
        HookWheel(Scroller)
        Root.Children.Add(Scroller)

        ' ---- 输入区 ----
        InputBox = New TextBox With {
            .Height = 54,
            .AcceptsReturn = True,
            .TextWrapping = TextWrapping.Wrap,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .Margin = New Thickness(0, 8, 0, 6)
        }
        StyleInput(InputBox, 54.0)

        ' 用 handledEventsToo:=True 注册（VB 的 AddHandler 语句没这个参数，只能调方法）：
        ' AcceptsReturn 的 TextBox 会先在类处理器里把回车吃掉。
        InputBox.AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent, New System.Windows.Input.KeyEventHandler(AddressOf InputBox_KeyDown), True)
        InputBox.AddHandler(System.Windows.Input.Keyboard.KeyDownEvent, New System.Windows.Input.KeyEventHandler(AddressOf InputBox_KeyDown), True)
        ' 跟踪输入法组字：组字中的回车是「上屏」，不能当发送
        InputBox.AddHandler(System.Windows.Input.TextCompositionManager.TextInputStartEvent, New System.Windows.Input.TextCompositionEventHandler(AddressOf InputImeStart), True)
        InputBox.AddHandler(System.Windows.Input.TextCompositionManager.TextInputUpdateEvent, New System.Windows.Input.TextCompositionEventHandler(AddressOf InputImeStart), True)
        InputBox.AddHandler(System.Windows.Input.TextCompositionManager.TextInputEvent, New System.Windows.Input.TextCompositionEventHandler(AddressOf InputImeDone), True)
        Root.Children.Add(InputBox)

        Dim BtnRow As New StackPanel With {.Orientation = Orientation.Horizontal}
        SendBtn = New PCL.MyButton With {.Text = "发送", .Height = 28, .MinWidth = 76, .Margin = New Thickness(0, 0, 6, 0)}
        AddHandler SendBtn.Click, Sub(sender, e) Send()
        BtnRow.Children.Add(SendBtn)

        StopBtn = New PCL.MyButton With {.Text = "停止", .Height = 28, .MinWidth = 76, .Margin = New Thickness(0, 0, 10, 0), .IsEnabled = False}
        AddHandler StopBtn.Click,
            Sub(sender, e)
                Try
                    If Cts IsNot Nothing Then Cts.Cancel()
                Catch ex As Exception
                End Try
            End Sub
        BtnRow.Children.Add(StopBtn)

        Dim Tip As New TextBlock With {
            .Text = "Enter 发送 · /help 看指令",
            .FontSize = 11,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Tip.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        BtnRow.Children.Add(Tip)
        Root.Children.Add(BtnRow)

        RenderAll()
        UpdateStatus()
        Return Root
    End Function

    ''' <summary>往上找最近的外层 ScrollViewer（就是宿主页面的滚动器）。</summary>
    Private Function FindOuterScroller(D As DependencyObject) As ScrollViewer
        Try
            Dim C = VisualTreeHelper.GetParent(D)
            While C IsNot Nothing
                Dim S = TryCast(C, ScrollViewer)
                If S IsNot Nothing Then Return S
                C = VisualTreeHelper.GetParent(C)
            End While
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 让卡片内的滚动区自己吃滚轮。宿主的页面滚动器会带着动画把整页滚走，而且不理会
    ''' Handled，所以改为「鼠标停在这块区域时把页面钉住」：进区记下外层偏移，滚轮只滚自己，
    ''' 外层一旦被动画带走就拉回来；鼠标离开即解锁。
    ''' </summary>
    Private Sub HookWheel(Sv As PCL.MyScrollViewer)
        If Sv Is Nothing Then Return
        ' 建卡时还没挂进可视化树，找不到外层滚动器；等 Loaded 再挂。
        AddHandler Sv.Loaded,
            Sub(sender, e)
                HookWheelNow(Sv)
            End Sub
    End Sub

    Private Sub HookWheelNow(Sv As PCL.MyScrollViewer)
        If Sv Is Nothing Then Return
        Dim Outer As ScrollViewer = FindOuterScroller(Sv)
        ' PCL 自己的 PerformVerticalOffsetDelta 是带动画的平滑滚动，直接调它比自己改 Offset 手感好。
        Dim DoScroll As Action(Of Double) =
            Sub(Delta As Double)
                Try
                    Sv.PerformVerticalOffsetDelta(Delta)
                Catch exS As Exception
                    Try
                        Sv.ScrollToVerticalOffset(Sv.VerticalOffset + Delta)
                    Catch ex2 As Exception
                    End Try
                End Try
            End Sub
        Dim StepOf As Func(Of Integer, Double) =
            Function(WheelDelta As Integer)
                Dim M1 As Double = 1.0
                Try
                    M1 = CDbl(Sv.DeltaMult)
                Catch ex1 As Exception
                End Try
                Return -WheelDelta * M1 / 3.0
            End Function

        If Outer Is Nothing Then
            Sv.AddHandler(System.Windows.UIElement.PreviewMouseWheelEvent,
                New System.Windows.Input.MouseWheelEventHandler(
                    Sub(sender, e)
                        DoScroll(StepOf(e.Delta))
                        e.Handled = True
                    End Sub), True)
            Return
        End If

        Dim Pinned As Double = -1
        AddHandler Sv.MouseEnter, Sub(sender, e) Pinned = Outer.VerticalOffset
        AddHandler Sv.MouseLeave, Sub(sender, e)
                                      Pinned = -1
                                  End Sub
        AddHandler Outer.ScrollChanged,
            Sub(sender, e3)
                If Pinned >= 0 AndAlso Sv.IsMouseOver Then
                    If Math.Abs(Outer.VerticalOffset - Pinned) > 0.5 Then
                        Outer.ScrollToVerticalOffset(Pinned)
                    End If
                End If
            End Sub
        Sv.AddHandler(System.Windows.UIElement.PreviewMouseWheelEvent,
            New System.Windows.Input.MouseWheelEventHandler(
                Sub(sender, e)
                    If Pinned < 0 Then Pinned = Outer.VerticalOffset
                    DoScroll(StepOf(e.Delta))
                    e.Handled = True
                End Sub), True)
    End Sub

    ''' <summary>普通 TextBox 是系统白底，统一贴到卡片配色。</summary>
    Private Sub StyleInput(Box As TextBox, H As Double)
        If Box Is Nothing Then Return
        Box.Height = H
        Box.Background = New SolidColorBrush(Color.FromArgb(18, 255, 255, 255))
        Box.BorderBrush = New SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        Box.BorderThickness = New Thickness(1)
        Box.Padding = New Thickness(8, 4, 8, 4)
        Box.FontSize = 13
        Box.VerticalContentAlignment = If(H > 40, VerticalAlignment.Top, VerticalAlignment.Center)
        Box.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1")
        Box.SetResourceReference(TextBox.CaretBrushProperty, "ColorBrush1")
    End Sub
    Private Function MakeFieldRow(Label As String, Box As TextBox, Initial As String) As StackPanel
        Dim Row As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim Lb As New TextBlock With {
            .Text = Label,
            .FontSize = 12,
            .Width = 34,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Lb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
        Row.Children.Add(Lb)
        Box.Text = If(Initial, "")
        Box.Width = 250
        StyleInput(Box, 26.0)
        Row.Children.Add(Box)
        Return Row
    End Function

    ''' <summary>输入法正在组字（拼音还没上屏）。</summary>
    Private ImeComposing As Boolean

    Private Sub InputImeStart(sender As Object, e As System.Windows.Input.TextCompositionEventArgs)
        ImeComposing = True
    End Sub

    Private Sub InputImeDone(sender As Object, e As System.Windows.Input.TextCompositionEventArgs)
        ImeComposing = False
    End Sub

    Private Sub InputBox_KeyDown(sender As Object, e As System.Windows.Input.KeyEventArgs)
        ' 中文输入法开着时，回车会以 ImeProcessed 形式过来，真实按键要看 ImeProcessedKey；
        ' 小键盘回车是 Key.Enter，主键盘是 Key.Return，两个都得认。
        Dim K = If(e.Key = System.Windows.Input.Key.ImeProcessed, e.ImeProcessedKey, e.Key)
        ' 组字中的回车要留给输入法上屏
        If e.Key = System.Windows.Input.Key.ImeProcessed AndAlso ImeComposing Then Return
        If (K = System.Windows.Input.Key.Return OrElse K = System.Windows.Input.Key.Enter) AndAlso
           (System.Windows.Input.Keyboard.Modifiers And System.Windows.Input.ModifierKeys.Shift) = 0 Then
            e.Handled = True
            Send()
        End If
    End Sub

    ' ==================== 状态 / 用量 ====================

    ''' <summary>粗略估 token：中文约 0.6 token/字，英文约 0.3。</summary>
    Private Function EstimateTokens() As Integer
        Dim Chars As Integer = 0
        For Each M In Messages
            Chars += If(M.Content, "").Length
        Next
        Chars += If(SysPrompt, "").Length
        Return CInt(Chars * 0.55)
    End Function

    Private Function FmtTok(N As Integer) As String
        If N >= 1000 Then Return (N / 1000.0).ToString("0.0") & "k"
        Return N.ToString()
    End Function

    Private Sub UpdateStatus()
        If StatusText Is Nothing Then Return
        Dim KeyState As String = "未配置密钥"
        If ApiKey.Length > 0 Then KeyState = "已配置密钥"
        If IsLocalEndpoint() Then KeyState = "本地接口"
        StatusText.Text = $"{ModelName} · {KeyState} · {Messages.Count} 条" &
                          If(LastToolCount > 0, $" · 工具 {LastToolCount}", "")

        If UsageText IsNot Nothing Then
            Dim Used As Integer = EstimateTokens()
            Dim Pct As Double = 0
            If CtxLimit > 0 Then Pct = Math.Min(1.0, Used / CtxLimit)
            UsageText.Text = $"上下文 ≈ {FmtTok(Used)} / {FmtTok(CtxLimit)} ({CInt(Pct * 100)}%)" &
                             If(LastPromptToks > 0, $" · 上次 ↑{LastPromptToks} ↓{LastCompletionToks}", "")
        End If

        If CtxGrid IsNot Nothing AndAlso CtxGrid.ColumnDefinitions.Count = 2 Then
            Dim Used As Integer = EstimateTokens()
            Dim Pct As Double = 0
            If CtxLimit > 0 Then Pct = Math.Min(1.0, Used / CtxLimit)
            If Pct < 0.0001 Then Pct = 0.0001
            CtxGrid.ColumnDefinitions(0).Width = New GridLength(Pct, GridUnitType.Star)
            CtxGrid.ColumnDefinitions(1).Width = New GridLength(1.0 - Pct, GridUnitType.Star)
        End If
    End Sub

    ' ==================== 渲染 ====================

    Private Sub RenderAll()
        If MsgPanel Is Nothing Then Return
        MsgPanel.Children.Clear()
        If Messages.Count = 0 Then
            AddTransient("在这里问我点什么吧。第一次用先点「配置」填接口和密钥。" & Environment.NewLine &
                         "我可以真的操作启动器：/instances 列版本、/launch 启动、/jvm 加参数……输入 /help 看全部指令。")
            Return
        End If
        For Each M In Messages
            If String.IsNullOrWhiteSpace(M.Content) Then Continue For
            AddBubble(M.Role, M.Content)
        Next
        ScrollToEnd()
    End Sub

    ''' <summary>把消息渲染成气泡，返回气泡内容面板（流式时反复重填）。</summary>
    Private Function AddBubble(Role As String, Text As String) As StackPanel
        If MsgPanel Is Nothing Then Return Nothing
        Dim IsUser As Boolean = (Role = "user")
        Dim Brd As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Padding = New Thickness(10, 7, 10, 7),
            .Margin = New Thickness(0, 0, 0, 8),
            .MaxWidth = 468,
            .HorizontalAlignment = If(IsUser, HorizontalAlignment.Right, HorizontalAlignment.Left),
            .Background = If(IsUser,
                New SolidColorBrush(Color.FromArgb(30, 96, 140, 255)),
                New SolidColorBrush(Color.FromArgb(22, 128, 128, 128)))
        }
        Dim Sp As New StackPanel()
        FillBubble(Sp, Text)
        Brd.Child = Sp
        MsgPanel.Children.Add(Brd)
        Return Sp
    End Function

    ''' <summary>只出现在界面、不写进历史的提示气泡（实例列表、指令回显等）。</summary>
    Private Function AddTransient(Text As String) As StackPanel
        If MsgPanel Is Nothing Then Return Nothing
        Dim Brd As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Padding = New Thickness(10, 7, 10, 7),
            .Margin = New Thickness(0, 0, 0, 8),
            .MaxWidth = 468,
            .HorizontalAlignment = HorizontalAlignment.Left,
            .Background = New SolidColorBrush(Color.FromArgb(18, 128, 128, 128))
        }
        Dim Sp As New StackPanel()
        FillBubble(Sp, Text, True)
        Brd.Child = Sp
        MsgPanel.Children.Add(Brd)
        Return Sp
    End Function

    ''' <summary>按 ``` 围栏切分文本：普通段落用正文字体，代码段落用等宽块。</summary>
    Private Sub FillBubble(Sp As StackPanel, Text As String, Optional Faded As Boolean = False)
        If Sp Is Nothing Then Return
        Sp.Children.Clear()
        Dim T As String = If(Text, "")
        Dim I As Integer = 0
        Do While True
            Dim A As Integer = T.IndexOf(Fence, I, StringComparison.Ordinal)
            If A < 0 Then
                If I < T.Length Then AddPlain(Sp, T.Substring(I), Faded)
                Exit Do
            End If
            If A > I Then AddPlain(Sp, T.Substring(I, A - I), Faded)
            Dim B As Integer = T.IndexOf(Fence, A + Fence.Length, StringComparison.Ordinal)
            If B < 0 Then
                ' 还没收口（流式输出中）：整段当代码，后面继续追加
                AddCode(Sp, T.Substring(A + Fence.Length))
                Exit Do
            End If
            AddCode(Sp, T.Substring(A + Fence.Length, B - (A + Fence.Length)))
            I = B + Fence.Length
        Loop
        If Sp.Children.Count = 0 Then AddPlain(Sp, "", Faded)
    End Sub

    Private Sub AddPlain(Sp As StackPanel, Text As String, Faded As Boolean)
        Dim Clean As String = If(Text, "").TrimStart(Convert.ToChar(10))
        If Clean.Length = 0 Then Return
        Dim Tb As New TextBlock With {
            .Text = Clean.TrimEnd(Convert.ToChar(10)),
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 13,
            .LineHeight = 19
        }
        Tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        If Faded Then Tb.Opacity = 0.75
        Sp.Children.Add(Tb)
    End Sub

    ''' <summary>代码块：深色底 + 等宽字体，首行若是语言标记就当标签显示。</summary>
    Private Sub AddCode(Sp As StackPanel, Body As String)
        Dim Raw As String = If(Body, "").TrimStart(Convert.ToChar(10))
        Dim Lang As String = ""
        Dim Code As String = Raw
        Dim FirstNl As Integer = Raw.IndexOf(Convert.ToChar(10))
        If FirstNl > 0 AndAlso FirstNl <= 20 Then
            Dim Head As String = Raw.Substring(0, FirstNl).Trim()
            If Head.Length > 0 AndAlso Head.IndexOf(Convert.ToChar(32)) < 0 Then Lang = Head
            If Lang.Length > 0 Then Code = Raw.Substring(FirstNl + 1)
        End If
        Code = Code.TrimEnd(Convert.ToChar(10))
        ' 只有语言标记、没有正文的残片（旧版历史被撕碎后留下的）直接不画。
        If Code.Trim().Length = 0 AndAlso Lang.Length > 0 Then Return

        Dim Box As New StackPanel()
        If Lang.Length > 0 Then
            Dim Tag As New TextBlock With {
                .Text = Lang,
                .FontSize = 10,
                .Margin = New Thickness(0, 0, 0, 3),
                .Opacity = 0.6
            }
            Tag.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
            Box.Children.Add(Tag)
        End If
        Dim Tb As New TextBlock With {
            .Text = Code,
            .TextWrapping = TextWrapping.Wrap,
            .FontFamily = New FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
            .FontSize = 12,
            .LineHeight = 17
        }
        Tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Box.Children.Add(Tb)

        Dim Brd As New Border With {
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(9, 7, 9, 7),
            .Margin = New Thickness(0, 3, 0, 3),
            .Background = New SolidColorBrush(Color.FromArgb(56, 0, 0, 0)),
            .BorderBrush = New SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            .BorderThickness = New Thickness(1),
            .Child = Box
        }
        Sp.Children.Add(Brd)
    End Sub

    Private Sub ScrollToEnd()
        If Scroller Is Nothing Then Return
        Try
            If MsgPanel IsNot Nothing Then MsgPanel.UpdateLayout()
            Scroller.UpdateLayout()
            Scroller.ScrollToVerticalOffset(Scroller.ScrollableHeight)
            ' 首屏渲染时布局还没算完，ScrollableHeight 会偏小；再补一帧。
            Scroller.Dispatcher.BeginInvoke(
                New Action(Sub()
                              Try
                                  If MsgPanel IsNot Nothing Then MsgPanel.UpdateLayout()
                                  Scroller.UpdateLayout()
                                  Scroller.ScrollToVerticalOffset(Scroller.ScrollableHeight)
                              Catch ex2 As Exception
                              End Try
                          End Sub),
                System.Windows.Threading.DispatcherPriority.ContextIdle)
        Catch ex As Exception
        End Try
    End Sub

    ' ==================== 发送与流式接收 ====================

    Private Sub Send()
        If Busy Then Return
        If Host Is Nothing Then Return
        LoadSettings()
        If InputBox Is Nothing Then Return

        Dim Text As String = If(InputBox.Text, "").Trim()
        If Text.Length = 0 Then Return

        ' 斜杠指令走本地，不发请求
        If Text.StartsWith("/") Then
            InputBox.Text = ""
            HandleCommand(Text)
            Return
        End If

        If ApiKey.Length = 0 AndAlso Not IsLocalEndpoint() Then
            UpdateStatus()
            If ConfPanel IsNot Nothing Then ConfPanel.Visibility = Visibility.Visible
            Host.Hint("先在「配置」里填 API Key，或用本地 Ollama")
            Return
        End If

        InputBox.Text = ""

        Dim WasEmpty As Boolean = (Messages.Count = 0)
        Messages.Add(New ChatMessage With {.Role = "user", .Content = Text})
        If WasEmpty AndAlso MsgPanel IsNot Nothing Then MsgPanel.Children.Clear()
        AddBubble("user", Text)

        Dim Target As New ChatMessage With {.Role = "assistant", .Content = ""}
        Messages.Add(Target)
        CurTarget = Target
        CurBubble = AddBubble("assistant", "")
        ScrollToEnd()

        Dim Extra As New List(Of String)
        Dim ForceTool As Boolean = LooksLikeAction(Text)
        Dim ToolsUsed As Integer = 0
        Busy = True
        SetBusyUi(True)
        UpdateStatus()

        Cts = New CancellationTokenSource()
        Dim Tok As CancellationToken = Cts.Token

        Task.Run(Async Function() As Task
                     Try
                         Dim Rounds As Integer = 0
                         Do
                             Tok.ThrowIfCancellationRequested()
                             ToolCalls.Clear()
                             Dim Choice As String = "auto"
                             If ForceTool AndAlso Rounds = 0 Then Choice = "required"
                             Dim Body As String = BuildRequestJson(Extra, Choice)
                             Await StreamChatAsync(Body, Target, Tok).ConfigureAwait(False)
                             If ToolCalls.Count = 0 Then Exit Do

                             Rounds += 1
                             If Rounds > MaxToolRounds Then
                                 AppendPiece(Target, Environment.NewLine & "（工具调用轮次已达上限，先停在这里）")
                                 Exit Do
                             End If

                             Extra.Add(ToolCallsMessageJson())
                             For Each Tc In ToolCalls
                                 Dim R As String = ExecTool(Tc.Name, Tc.Args.ToString())
                                 ToolsUsed += 1
                                 If Host IsNot Nothing Then Host.LogInfo("AI 工具 " & Tc.Name & " → " & Shorten(R, 140))
                                 Extra.Add(ToolResultMessageJson(Tc.Id, R))
                             Next
                             FlushUi()
                         Loop
                         If ToolsUsed = 0 AndAlso ForceTool Then
                             AppendPiece(Target, Environment.NewLine & Environment.NewLine &
                                         "⚠ 这一轮没有调用任何工具，上面的内容不是工具返回的真实结果，请让我重试或换个问法。")
                         End If
                         LastToolCount = ToolsUsed
                     Catch ex As OperationCanceledException
                         AppendPiece(Target, Environment.NewLine & "（已停止）")
                     Catch ex As Exception
                         If Host IsNot Nothing Then Host.LogWarn("AI 助手请求失败：" & ex.Message & " | " & ex.ToString())
                         AppendPiece(Target, "⚠ " & ex.Message)
                     Finally
                         FlushUi()
                         Busy = False
                         SaveHistory()
                         If Host IsNot Nothing Then
                             Host.RunInUi(
                                 Sub()
                                     SetBusyUi(False)
                                     UpdateStatus()
                                     ScrollToEnd()
                                 End Sub)
                         End If
                     End Try
                 End Function)
    End Sub

    Private Sub SetBusyUi(Active As Boolean)
        If SendBtn IsNot Nothing Then SendBtn.IsEnabled = Not Active
        If StopBtn IsNot Nothing Then StopBtn.IsEnabled = Active
    End Sub

    Private Async Function StreamChatAsync(Body As String, Target As ChatMessage, Tok As CancellationToken) As Task
        Dim Req As New HttpRequestMessage(HttpMethod.Post, ApiEndpoint())
        Req.Content = New StringContent(Body, Encoding.UTF8, "application/json")
        If ApiKey.Length > 0 Then
            Req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & ApiKey)
        End If
        Req.Headers.TryAddWithoutValidation("Accept", "text/event-stream")

        Dim Resp As HttpResponseMessage
        Try
            Resp = Await Client.SendAsync(Req, HttpCompletionOption.ResponseHeadersRead, Tok).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            Throw
        Catch ex As Exception
            Throw New Exception(ConnectErrorText(ex))
        End Try
        If Not Resp.IsSuccessStatusCode Then
            Dim ErrBody As String = ""
            Try
                ErrBody = Await Resp.Content.ReadAsStringAsync().ConfigureAwait(False)
            Catch ex As Exception
            End Try
            Dim Tip As String = ""
            If CInt(Resp.StatusCode) = 404 Then
                Tip = "（404：多半是模型名不存在。用 ollama list 看已装模型，或在「配置」里改成已装的那个。）"
            ElseIf CInt(Resp.StatusCode) = 401 OrElse CInt(Resp.StatusCode) = 403 Then
                Tip = "（鉴权失败：检查 API Key 是否填对。）"
            End If
            Throw New Exception($"HTTP {CInt(Resp.StatusCode)}：{Shorten(ErrBody, 240)}{Tip}")
        End If

        Using St As Stream = Await Resp.Content.ReadAsStreamAsync().ConfigureAwait(False)
            Using Rd As New StreamReader(St, Encoding.UTF8)
                Do
                    Tok.ThrowIfCancellationRequested()
                    Dim Line As String = Await Rd.ReadLineAsync().ConfigureAwait(False)
                    If Line Is Nothing Then Exit Do
                    If Not Line.StartsWith("data:", StringComparison.Ordinal) Then Continue Do
                    Dim Data As String = Line.Substring(5).Trim()
                    If Data.Length = 0 Then Continue Do
                    If Data = "[DONE]" Then Exit Do
                    ReadUsage(Data)
                    ReadToolDeltas(Data)
                    Dim Piece As String = ExtractContent(Data)
                    If Piece IsNot Nothing AndAlso Piece.Length > 0 Then AppendPiece(Target, Piece)
                Loop
            End Using
        End Using
        FlushUi()
    End Function

    ''' <summary>从 SSE 块里读 usage（DeepSeek 在最后一帧返回 prompt/completion tokens）。</summary>
    Private Sub ReadUsage(Json As String)
        Try
            Dim P As Integer = Json.IndexOf("""prompt_tokens"":", StringComparison.Ordinal)
            If P < 0 Then Return
            Dim C As Integer = Json.IndexOf("""completion_tokens"":", StringComparison.Ordinal)
            Dim PT As Integer = ReadNumberAfter(Json, P + 16)
            Dim CT As Integer = 0
            If C >= 0 Then CT = ReadNumberAfter(Json, C + 20)
            If PT > 0 Then
                LastPromptToks = PT
                LastCompletionToks = CT
            End If
        Catch ex As Exception
        End Try
    End Sub

    Private Function ReadNumberAfter(S As String, Index As Integer) As Integer
        Dim I As Integer = Index
        While I < S.Length AndAlso (S(I) = Convert.ToChar(32) OrElse S(I) = Convert.ToChar(9))
            I += 1
        End While
        Dim Start As Integer = I
        While I < S.Length AndAlso Char.IsDigit(S(I))
            I += 1
        End While
        If I = Start Then Return 0
        Dim N As Integer
        If Integer.TryParse(S.Substring(Start, I - Start), N) Then Return N
        Return 0
    End Function

    Private Sub AppendPiece(Target As ChatMessage, Piece As String)
        SyncLock SyncRoot
            Target.Content = Target.Content & Piece
            Pending.Append(Piece)
        End SyncLock
        If (DateTime.UtcNow - LastFlush).TotalMilliseconds >= 90 Then FlushUi()
    End Sub

    Private Sub FlushUi()
        Dim Full As String
        SyncLock SyncRoot
            If Pending.Length = 0 Then
                LastFlush = DateTime.UtcNow
                Return
            End If
            Pending.Clear()
            Full = If(CurTarget Is Nothing, "", CurTarget.Content)
            LastFlush = DateTime.UtcNow
        End SyncLock
        If Host Is Nothing Then Return
        Host.RunInUi(
            Sub()
                If CurBubble IsNot Nothing Then FillBubble(CurBubble, Full)
                ScrollToEnd()
            End Sub)
    End Sub

    ' ==================== 斜杠指令 ====================

    Private Sub HandleCommand(Text As String)
        Dim Parts As String() = Text.Substring(1).Split(New Char() {Convert.ToChar(32)}, 2)
        Dim Cmd As String = Parts(0).ToLowerInvariant()
        Dim Arg As String = If(Parts.Length > 1, Parts(1).Trim(), "")

        Select Case Cmd
            Case "help", "帮助", "?"
                AddTransient("可用指令：" & Environment.NewLine &
                             "/instances  列出启动器里的游戏实例" & Environment.NewLine &
                             "/launch <名字>  启动指定实例" & Environment.NewLine &
                             "/jvm <参数>  给下次启动追加 JVM 参数" & Environment.NewLine &
                             "/args <参数>  给下次启动追加游戏参数" & Environment.NewLine &
                             "/args  （不带参数）  查看当前追加了什么" & Environment.NewLine &
                             "/clear  清空对话" & Environment.NewLine &
                             "其余内容都直接发给 AI。")
                ScrollToEnd()

            Case "instances", "实例", "list"
                AddTransient(InstanceListText())
                ScrollToEnd()

            Case "launch", "启动"
                If Arg.Length = 0 Then
                    AddTransient("用法：/launch <实例名片段>，例如 /launch 1.8.9；不带名字则启动当前实例。")
                    AddTransient(SelectAndLaunch(""))
                Else
                    AddTransient(SelectAndLaunch(Arg))
                End If
                ScrollToEnd()

            Case "jvm"
                If Arg.Length = 0 Then
                    AddTransient("当前追加的 JVM 参数：" & ListOrNone(Host.ExtraJvmArgs))
                Else
                    Host.AppendJvmArgs(Arg)
                    AddTransient("已追加 JVM 参数：" & Arg & Environment.NewLine &
                                 "现在共 " & Host.ExtraJvmArgs.Count & " 条，下次启动生效。")
                End If
                ScrollToEnd()

            Case "args", "gameargs"
                If Arg.Length = 0 Then
                    AddTransient("JVM 参数：" & ListOrNone(Host.ExtraJvmArgs) & Environment.NewLine &
                                 "游戏参数：" & ListOrNone(Host.ExtraGameArgs))
                Else
                    Host.AppendGameArgs(Arg)
                    AddTransient("已追加游戏参数：" & Arg & Environment.NewLine &
                                 "现在共 " & Host.ExtraGameArgs.Count & " 条，下次启动生效。")
                End If
                ScrollToEnd()

            Case "clear", "清空"
                Messages.Clear()
                SaveHistory()
                RenderAll()
                UpdateStatus()

            Case Else
                AddTransient("不认识的指令「/" & Cmd & "」，输入 /help 看列表。")
                ScrollToEnd()
        End Select
    End Sub

    Private Function ListOrNone(L As List(Of String)) As String
        If L Is Nothing OrElse L.Count = 0 Then Return "（无）"
        Return String.Join(" | ", L.ToArray())
    End Function

    ' ==================== 启动器实例 ====================

    ''' <summary>启动器当前的 MC 根目录：优先问宿主（McFolderSelected.Path），其次读 Setup.ini。</summary>
    ' ==================== 实例与内容（API v7.3） ====================

    Private Function AllInstances() As List(Of PCL.PluginInstanceEntry)
        Try
            Dim L = Host.ListInstances()
            If L IsNot Nothing Then Return L
        Catch ex As Exception
            Host.LogWarn("ListInstances 失败：" & ex.Message)
        End Try
        Return New List(Of PCL.PluginInstanceEntry)
    End Function

    Private Function FindInstEntry(Key As String) As PCL.PluginInstanceEntry
        Dim K As String = If(Key, "").Trim().ToLowerInvariant()
        If K.Length = 0 Then Return Nothing
        Dim All = AllInstances()
        For Each E In All
            If E.Name.ToLowerInvariant() = K Then Return E
        Next
        For Each E In All
            If E.Name.ToLowerInvariant().Contains(K) Then Return E
        Next
        Return Nothing
    End Function

    ''' <summary>切换实例（instance.select）+ 启动（launch）；名字留空表示启动当前实例。</summary>
    Private Function SelectAndLaunch(Name As String) As String
        Dim Want As String = If(Name, "").Trim()
        If Want.Length > 0 Then
            Dim Cur As PCL.PluginInstanceEntry = Nothing
            Try
                Cur = Host.SelectedInstance()
            Catch ex As Exception
            End Try
            If Cur Is Nothing OrElse Not String.Equals(Cur.Name, Want, StringComparison.OrdinalIgnoreCase) Then
                Dim Hit = FindInstEntry(Want)
                If Hit Is Nothing Then Return "没找到匹配「" & Want & "」的实例。可用的是：" & InstanceListText()
                If Not Host.SelectInstance(Hit.Name) Then Return "切换到「" & Hit.Name & "」失败（宿主返回 False）。"
                Want = Hit.Name
            End If
        End If
        Dim OkL As Boolean = Host.LaunchGame()
        Dim M As String = If(OkL, "已交给启动器开始启动", "启动器返回 False（可能有其它任务在跑，或实例配置不完整）")
        Host.LogInfo("AI 工具启动：" & If(Want.Length > 0, Want, "当前实例") & " → " & M)
        Return If(Want.Length > 0, Want & " → ", "") & M
    End Function

    ''' <summary>只切换当前实例，不启动。</summary>
    Private Function SelectOnly(Name As String) As String
        Dim Hit = FindInstEntry(Name)
        If Hit Is Nothing Then Return "没找到匹配「" & Name & "」的实例。可用的是：" & InstanceListText()
        If Not Host.SelectInstance(Hit.Name) Then Return "切换到「" & Hit.Name & "」失败（宿主返回 False）。"
        Host.LogInfo("AI 工具 select_instance → " & Hit.Name)
        Return "已把当前实例切到「" & Hit.Name & "」（不会自动启动；要启动请再调 launch_instance）。"
    End Function

    ' ==================== JSON ====================

    Private Function JsonStr(S As String) As String
        Dim Sb As New StringBuilder()
        Sb.Append(ChQuote)
        For Each C In If(S, "")
            Dim Code As Integer = AscW(C)
            If Code = 34 Then
                Sb.Append(ChBack).Append(ChQuote)
            ElseIf Code = 92 Then
                Sb.Append(ChBack).Append(ChBack)
            ElseIf Code = 10 Then
                Sb.Append(ChBack).Append(ChN)
            ElseIf Code = 13 Then
                Sb.Append(ChBack).Append(ChR)
            ElseIf Code = 9 Then
                Sb.Append(ChBack).Append(ChT)
            ElseIf Code < 32 Then
                Sb.Append(ChBack).Append(ChU).Append(Code.ToString("x4"))
            Else
                Sb.Append(C)
            End If
        Next
        Sb.Append(ChQuote)
        Return Sb.ToString()
    End Function

    Private Function BuildRequestJson(Optional Extra As List(Of String) = Nothing, Optional ToolChoice As String = "auto") As String
        Dim Sb As New StringBuilder()
        Sb.Append("{").Append(JsonStr("model")).Append(":").Append(JsonStr(ModelName))
        Sb.Append(",").Append(JsonStr("stream")).Append(":true")
        Sb.Append(",").Append(JsonStr("stream_options")).Append(":{").Append(JsonStr("include_usage")).Append(":true}")
        Sb.Append(",").Append(JsonStr("temperature")).Append(":0.3")
        Sb.Append(",").Append(JsonStr("tools")).Append(":").Append(ToolsJson())
        Sb.Append(",").Append(JsonStr("tool_choice")).Append(":").Append(JsonStr(ToolChoice))
        Sb.Append(",").Append(JsonStr("messages")).Append(":[")

        Dim Src As New List(Of ChatMessage)
        For Each M In Messages
            If M.Role = "assistant" AndAlso String.IsNullOrWhiteSpace(M.Content) Then Continue For
            Src.Add(M)
        Next

        Dim First As Boolean = True
        If Not String.IsNullOrWhiteSpace(SysPrompt) Then
            Sb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr("system"))
            Sb.Append(",").Append(JsonStr("content")).Append(":").Append(JsonStr(SysPrompt)).Append("}")
            First = False
        End If

        Dim StartIdx As Integer = Math.Max(0, Src.Count - 20)
        For I As Integer = StartIdx To Src.Count - 1
            If Not First Then Sb.Append(",")
            Sb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr(Src(I).Role))
            Sb.Append(",").Append(JsonStr("content")).Append(":").Append(JsonStr(Src(I).Content)).Append("}")
            First = False
        Next

        If Extra IsNot Nothing Then
            For Each Raw In Extra
                Sb.Append(",").Append(Raw)
            Next
        End If

        Sb.Append("]}")
        Return Sb.ToString()
    End Function

    ''' <summary>给模型的工具清单（OpenAI 兼容 function calling）。</summary>
    Private Function ToolsJson() As String
        Dim S As String = "[" &
            ToolDef("list_instances", "列出启动器里所有可启动的游戏实例（版本）。需要知道有哪些版本时先调这个。", "{}") & "," &
            ToolDef("instance_info", "查看启动器当前选中的游戏实例信息：名称、版本、加载器、目录、是否可装模组（只读）。", "{}") & "," &
            ToolDef2("launch_instance", "启动游戏。传 name 会先把当前实例切过去（instance.select）再启动；不传就启动当前实例。会真的把游戏跑起来，用户没明确要求启动时不要调。",
                     "{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""实例名（可省略，可用片段），省略表示启动当前实例""}}}") & "," &
            ToolDef2("select_instance", "只切换当前选中的游戏实例（会影响之后「启动游戏」用哪个版本），不会启动。",
                     "{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""实例名或片段""}},""required"":[""name""]}") & "," &
            ToolDef2("append_jvm_args", "给下一次启动追加 JVM 参数（本次会话有效）。",
                     "{""type"":""object"",""properties"":{""args"":{""type"":""string"",""description"":""要追加的参数，如 -Xmx4G -XX:+UseG1GC""}},""required"":[""args""]}") & "," &
            ToolDef2("append_game_args", "给下一次启动追加游戏参数（本次会话有效）。",
                     "{""type"":""object"",""properties"":{""args"":{""type"":""string"",""description"":""要追加的游戏参数""}},""required"":[""args""]}") & "," &
            ToolDef("get_pending_args", "查看当前已追加的 JVM 参数和游戏参数。", "{}") & "," &
            ToolDef("list_plugins", "列出启动器里已安装的插件及启用状态（只读）。", "{}") & "," &
            ToolDef2("set_plugin_enabled", "启用或禁用某个插件。注意：启动器会重载插件，包括本插件自己，聊天可能被打断；不能操作本插件。",
                     "{""type"":""object"",""properties"":{""id"":{""type"":""string"",""description"":""插件 id，先用 list_plugins 查""},""enabled"":{""type"":""boolean"",""description"":""true 启用 / false 禁用""}},""required"":[""id"",""enabled""]}") & "," &
            ToolDef2("list_mods", "列出当前实例（或指定实例）的 mods / resourcepacks / shaderpacks 内容，含启用状态（只读）。",
                     "{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""mods"",""resourcepacks"",""shaderpacks""],""description"":""默认 mods""},""instance"":{""type"":""string"",""description"":""实例名，默认当前实例""}}}") & "," &
            ToolDef2("set_mod_enabled", "启用或禁用某个模组/资源包/光影（只是把文件名改成 .disabled，可逆）。用户没明确要求时不要调。",
                     "{""type"":""object"",""properties"":{""file"":{""type"":""string"",""description"":""FileName，来自 list_mods，不要带 .disabled""},""enabled"":{""type"":""boolean"",""description"":""true 启用 / false 禁用""},""kind"":{""type"":""string"",""enum"":[""mods"",""resourcepacks"",""shaderpacks""],""description"":""默认 mods""},""instance"":{""type"":""string"",""description"":""实例名，默认当前实例""}},""required"":[""file"",""enabled""]}") & "," &
            ToolDef2("read_log", "读当前实例的游戏日志（只读，最多 200 行）。排查游戏崩溃/模组报错时用这个；启动器自己的日志不对模型开放。",
                     "{""type"":""object"",""properties"":{""lines"":{""type"":""integer"",""description"":""要读多少行，默认 60，上限 200""}}}") & "," &
            ToolDef2("global_setting", "读取启动器全局设置里某一项的值（只读，如 UiLanguage、UiTheme 之类，键名不确定就先问用户）。",
                     "{""type"":""object"",""properties"":{""key"":{""type"":""string"",""description"":""设置项键名""}},""required"":[""key""]}") & "," &
            ToolDef2("write_note", "把一段文本存成笔记文件。只能写进本插件的 notes 目录，文件名限字母数字下划线连字符，扩展名 .md 或 .txt，内容上限 64 KB。",
                     "{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""文件名，如 今天的问题.md""},""text"":{""type"":""string"",""description"":""正文""}},""required"":[""name"",""text""]}") & "," &
            ToolDef2("open_url", "用系统浏览器打开一个 http/https 链接（不能是 file: 或其他协议）。",
                     "{""type"":""object"",""properties"":{""url"":{""type"":""string"",""description"":""完整网址""}},""required"":[""url""]}") & "]"
        Return S
    End Function

    Private Function ToolDef(Name As String, Desc As String, Params As String) As String
        Return ToolDef2(Name, Desc, Params)
    End Function

    Private Function ToolDef2(Name As String, Desc As String, Params As String) As String
        Return "{" & JsonStr("type") & ":" & JsonStr("function") & "," & JsonStr("function") & ":{" &
               JsonStr("name") & ":" & JsonStr(Name) & "," &
               JsonStr("description") & ":" & JsonStr(Desc) & "," &
               JsonStr("parameters") & ":" & Params & "}}"
    End Function

    ''' <summary>从一段 SSE JSON 里抠出 delta.content（没有就返回 Nothing）。</summary>
    Private Function ExtractContent(Json As String) As String
        Return JsonStringField(Json, "content")
    End Function

    ''' <summary>从一个 JSON 片段里取某个键的字符串值；没有该键就返回 Nothing。</summary>
    Private Function JsonStringField(Json As String, KeyName As String) As String
        Dim Key As String = """" & KeyName & """:"
        Dim I As Integer = Json.IndexOf(Key, StringComparison.Ordinal)
        If I < 0 Then Return Nothing
        I += Key.Length
        While I < Json.Length AndAlso (Json(I) = Convert.ToChar(32) OrElse Json(I) = Convert.ToChar(9))
            I += 1
        End While
        If I >= Json.Length OrElse Json(I) <> ChQuote Then Return Nothing
        I += 1

        Dim Sb As New StringBuilder()
        While I < Json.Length
            Dim C As Char = Json(I)
            If C = ChQuote Then Exit While
            If C = ChBack AndAlso I + 1 < Json.Length Then
                I += 1
                Dim N As Char = Json(I)
                If N = ChN Then
                    Sb.Append(Convert.ToChar(10))
                ElseIf N = ChT Then
                    Sb.Append(Convert.ToChar(9))
                ElseIf N = ChR Then
                    Sb.Append(Convert.ToChar(13))
                ElseIf N = ChB Then
                    Sb.Append(Convert.ToChar(8))
                ElseIf N = ChF Then
                    Sb.Append(Convert.ToChar(12))
                ElseIf N = ChU Then
                    If I + 4 < Json.Length Then
                        Dim Hex As String = Json.Substring(I + 1, 4)
                        Dim Code As Integer
                        If Integer.TryParse(Hex, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, Code) Then
                            Sb.Append(ChrW(Code))
                        End If
                        I += 4
                    End If
                Else
                    Sb.Append(N)
                End If
            Else
                Sb.Append(C)
            End If
            I += 1
        End While
        Return Sb.ToString()
    End Function

    Private Function Shorten(S As String, Max As Integer) As String
        If String.IsNullOrEmpty(S) Then Return ""
        Dim T = S.Replace(Environment.NewLine, " ")
        If T.Length <= Max Then Return T
        Return T.Substring(0, Max) & "…"
    End Function

    ' ==================== 工具调用 ====================

    ''' <summary>
    ''' 流式下的 tool_calls 是「按 index 分片」推送的：name 只在第一片出现，arguments 分多片拼。
    ''' 这里按 "index": 切段，把 name/arguments/id 累积到 ToolCalls 对应槽位。
    ''' </summary>
    Private Sub ReadToolDeltas(Json As String)
        Dim I As Integer = Json.IndexOf("""tool_calls"":", StringComparison.Ordinal)
        If I < 0 Then Return
        Dim P As Integer = Json.IndexOf("""index"":", I, StringComparison.Ordinal)
        While P >= 0
            Dim Slot As Integer = ReadNumberAfter(Json, P + 8)
            Dim NextP As Integer = Json.IndexOf("""index"":", P + 8, StringComparison.Ordinal)
            Dim Seg As String = If(NextP < 0, Json.Substring(P), Json.Substring(P, NextP - P))

            While ToolCalls.Count <= Slot
                ToolCalls.Add(New ToolCallReq())
            End While
            Dim Tc As ToolCallReq = ToolCalls(Slot)

            Dim Id As String = JsonStringField(Seg, "id")
            If Id IsNot Nothing AndAlso Id.Length > 0 Then Tc.Id = Id
            Dim Nm As String = JsonStringField(Seg, "name")
            If Nm IsNot Nothing AndAlso Nm.Length > 0 Then Tc.Name = Nm
            Dim Aa As String = JsonStringField(Seg, "arguments")
            If Aa IsNot Nothing Then Tc.Args.Append(Aa)

            P = NextP
        End While
    End Sub

    ''' <summary>拼一条 assistant 消息（带 tool_calls）给下一轮请求用。</summary>
    Private Function ToolCallsMessageJson() As String
        Dim Sb As New StringBuilder()
        Sb.Append("{").Append(JsonStr("role")).Append(":").Append(JsonStr("assistant"))
        Sb.Append(",").Append(JsonStr("content")).Append(":null")
        Sb.Append(",").Append(JsonStr("tool_calls")).Append(":[")
        Dim First As Boolean = True
        For Each Tc In ToolCalls
            If Not First Then Sb.Append(",")
            Sb.Append("{").Append(JsonStr("id")).Append(":").Append(JsonStr(Tc.Id))
            Sb.Append(",").Append(JsonStr("type")).Append(":").Append(JsonStr("function"))
            Sb.Append(",").Append(JsonStr("function")).Append(":{")
            Sb.Append(JsonStr("name")).Append(":").Append(JsonStr(Tc.Name))
            Sb.Append(",").Append(JsonStr("arguments")).Append(":").Append(JsonStr(Tc.Args.ToString()))
            Sb.Append("}}")
            First = False
        Next
        Sb.Append("]}")
        Return Sb.ToString()
    End Function

    Private Function ToolResultMessageJson(Id As String, Result As String) As String
        Return "{" & JsonStr("role") & ":" & JsonStr("tool") & "," &
               JsonStr("tool_call_id") & ":" & JsonStr(Id) & "," &
               JsonStr("content") & ":" & JsonStr(Result) & "}"
    End Function

    ''' <summary>执行一个工具，返回给模型的文本结果（永远不抛，失败也回一条说明）。</summary>
    Private Function ExecTool(Name As String, ArgsJson As String) As String
        Try
            Select Case If(Name, "").Trim().ToLowerInvariant()
                Case "list_instances"
                    Return InstanceListText()
                Case "instance_info"
                    Dim I = Host.InstanceInfo
                    If I Is Nothing OrElse Not I.Available Then Return "当前没有选中的游戏实例。"
                    Dim Sb0 As New StringBuilder()
                    Sb0.Append("名称：").Append(I.Name).Append("；版本：").Append(I.VersionId)
                    If Not String.IsNullOrWhiteSpace(I.InheritName) Then Sb0.Append("；继承自：").Append(I.InheritName)
                    Sb0.Append("；可装模组：").Append(If(I.Modable, "是", "否"))
                    If Not String.IsNullOrWhiteSpace(I.PathIndie) Then Sb0.Append("；目录：").Append(I.PathIndie)
                    Return Sb0.ToString()
                Case "global_setting"
                    Dim Gk As String = If(JsonStringField(ArgsJson, "key"), "").Trim()
                    If Gk.Length = 0 Then Return "缺少 key 参数。"
                    Dim Gv As String = Host.GetGlobalSetting(Gk)
                    If Gv Is Nothing Then Return "没有名为「" & Gk & "」的全局设置。"
                    If Gv.Trim().Length = 0 Then Return Gk & " = （空，未设置）"
                    Return Gk & " = " & Gv
                Case "launch_instance"
                    Dim Nm As String = If(JsonStringField(ArgsJson, "name"), "").Trim()
                    Return SelectAndLaunch(Nm)
                Case "select_instance"
                    Dim Nm2 As String = If(JsonStringField(ArgsJson, "name"), "").Trim()
                    If Nm2.Length = 0 Then Return "缺少 name 参数。"
                    Return SelectOnly(Nm2)
                Case "append_jvm_args"
                    Dim A As String = JsonStringField(ArgsJson, "args")
                    If String.IsNullOrWhiteSpace(A) Then Return "缺少 args 参数。"
                    Host.AppendJvmArgs(A.Trim())
                    Return "已追加 JVM 参数：" & A.Trim()
                Case "append_game_args"
                    Dim A As String = JsonStringField(ArgsJson, "args")
                    If String.IsNullOrWhiteSpace(A) Then Return "缺少 args 参数。"
                    Host.AppendGameArgs(A.Trim())
                    Return "已追加游戏参数：" & A.Trim()
                Case "get_pending_args"
                    Dim J As String = JoinList(Host.ExtraJvmArgs)
                    Dim G As String = JoinList(Host.ExtraGameArgs)
                    Return "JVM 参数：" & J & "；游戏参数：" & G
                Case "list_plugins"
                    Return PluginListText()
                Case "set_plugin_enabled"
                    Return ToolSetPluginEnabled(ArgsJson)
                Case "list_mods"
                    Return ModsListText(ArgsJson)
                Case "set_mod_enabled"
                    Return ToolSetMod(ArgsJson)
                Case "read_log"
                    Return ToolReadLog(ArgsJson)
                Case "write_note"
                    Return ToolWriteNote(ArgsJson)
                Case "open_url"
                    Return ToolOpenUrl(ArgsJson)
                Case Else
                    Return "未知工具：" & Name
            End Select
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "工具执行失败：" & T.Message
        End Try
    End Function

    Private Function JoinList(L As IEnumerable(Of String)) As String
        If L Is Nothing Then Return "（无）"
        Dim Parts As New List(Of String)
        For Each S In L
            If Not String.IsNullOrWhiteSpace(S) Then Parts.Add(S)
        Next
        If Parts.Count = 0 Then Return "（无）"
        Return String.Join(" ", Parts) & $"（{Parts.Count} 条）"
    End Function

    ''' <summary>笔记目录：只落在本插件自己的 data 文件夹里，是唯一允许写的目标。</summary>
    Private Function NotesDir() As String
        Return Path.Combine(Host.DataFolder, "notes")
    End Function

    ''' <summary>严格白名单：字母数字下划线中划线 + 单一扩展名 .md/.txt，不允许任何路径分隔符或多余点。</summary>
    Private Function SafeNoteName(Nm As String) As Boolean
        If String.IsNullOrWhiteSpace(Nm) Then Return False
        Nm = Nm.Trim()
        If Nm.Length < 5 OrElse Nm.Length > 48 Then Return False
        Dim Low As String = Nm.ToLowerInvariant()
        Dim Base As String
        If Low.EndsWith(".md") Then
            Base = Nm.Substring(0, Nm.Length - 3)
        ElseIf Low.EndsWith(".txt") Then
            Base = Nm.Substring(0, Nm.Length - 4)
        Else
            Return False
        End If
        If Base.Length = 0 Then Return False
        For Each C In Base
            Dim Ok As Boolean = Char.IsLetterOrDigit(C) OrElse C = Convert.ToChar(95) OrElse C = Convert.ToChar(45) OrElse AscW(C) > 127
            If Not Ok Then Return False
        Next
        If Nm.IndexOf(Convert.ToChar(92)) >= 0 OrElse Nm.IndexOf("/"c) >= 0 Then Return False
        Return True
    End Function

    Private Function JsonBoolField(Json As String, KeyName As String) As Boolean?
        Dim K As String = """" & KeyName & """:"
        Dim I As Integer = If(Json, "").IndexOf(K, StringComparison.Ordinal)
        If I < 0 Then Return Nothing
        Dim Rest As String = Json.Substring(I + K.Length).TrimStart()
        If Rest.StartsWith("true", StringComparison.OrdinalIgnoreCase) Then Return True
        If Rest.StartsWith("false", StringComparison.OrdinalIgnoreCase) Then Return False
        Return Nothing
    End Function

    Private Function JsonIntField(Json As String, KeyName As String, DefaultValue As Integer) As Integer
        Dim K As String = """" & KeyName & """:"
        Dim I As Integer = If(Json, "").IndexOf(K, StringComparison.Ordinal)
        If I < 0 Then Return DefaultValue
        Dim Rest As String = Json.Substring(I + K.Length).TrimStart()
        Dim N As Integer = 0
        Dim Got As Boolean = False
        For Each C In Rest
            If Char.IsDigit(C) Then
                N = N * 10 + (AscW(C) - AscW("0"c))
                Got = True
            Else
                Exit For
            End If
        Next
        If Got Then Return N
        Return DefaultValue
    End Function

    Private Function PluginListText() As String
        Try
            Dim All = Host.Plugins.List()
            If All Is Nothing OrElse All.Count = 0 Then Return "没有装任何插件。"
            Dim Sb As New StringBuilder()
            Sb.Append("共 ").Append(All.Count).Append(" 个插件：")
            For Each P In All
                Sb.Append(Environment.NewLine)
                Sb.Append(P.Id).Append(" | ").Append(P.Name).Append(" v").Append(P.Version)
                Sb.Append(If(P.Enabled, " | 已启用", " | 已禁用"))
                Sb.Append(If(P.Loaded, " | 已加载", " | 未加载"))
            Next
            Return Sb.ToString()
        Catch ex As Exception
            Return "读插件列表失败：" & ex.Message
        End Try
    End Function

    ''' <summary>开关插件。防护：拒绝操作自己、拒绝不存在的 id，且必须真的在列表里查到。</summary>
    Private Function ToolSetPluginEnabled(ArgsJson As String) As String
        Dim Id As String = If(JsonStringField(ArgsJson, "id"), "").Trim()
        Dim En As Boolean? = JsonBoolField(ArgsJson, "enabled")
        If Id.Length = 0 Then Return "缺少 id 参数，先用 list_plugins 查。"
        If Not En.HasValue Then Return "缺少 enabled 参数（true/false）。"
        If Id.Equals("aichat", StringComparison.OrdinalIgnoreCase) Then Return "拒绝：不能开关本插件自己（会把对话打断）。"
        Dim Target As Object = Nothing
        Try
            Target = Host.Plugins.Find(Id)
        Catch ex As Exception
        End Try
        If Target Is Nothing Then Return "没有 id 为「" & Id & "」的插件。现有：" & PluginListText()
        ' 官方文档：SetEnabled 会读写磁盘 + Refresh，必须在后台线程调用（这里本来就在 worker 线程上）
        Try
            Dim Ok As Boolean = Host.Plugins.SetEnabled(Id, En.Value)
            If Not Ok Then Return "启动器拒绝了这次变更：" & Id
        Catch ex2 As Exception
            Dim T2 As Exception = If(ex2.InnerException IsNot Nothing, ex2.InnerException, ex2)
            Return "变更失败：" & T2.Message
        End Try
        ' 复核：宿主可能返回成功但实际没变（reload 可能正在发生）
        Try
            Dim Re = Host.Plugins.Find(Id)
            If Re Is Nothing Then Return "已" & If(En.Value, "启用", "禁用") & " " & Id & "（复核跳过：插件列表里暂时找不到它，正在重载）。"
            If Re.Enabled <> En.Value Then
                Return "已调用宿主，但复核结果不一致：" & Id & " 现在仍是" & If(Re.Enabled, "启用", "禁用") & " 状态。"
            End If
            Return "已" & If(En.Value, "启用", "禁用") & " " & Id & "（列表复核通过；启动器会重载插件，本对话可能被打断）"
        Catch ex3 As Exception
            Dim T3 As Exception = If(ex3.InnerException IsNot Nothing, ex3.InnerException, ex3)
            Return "已" & If(En.Value, "启用", "禁用") & " " & Id & "（复核失败：" & T3.Message & "）"
        End Try
    End Function

    ''' <summary>按 kind 取内容列表（mods / resourcepacks / shaderpacks）。</summary>
    Private Function ContentsOf(Kind As String, Opt As String) As List(Of PCL.PluginContentEntry)
        Select Case Kind
            Case "mods"
                Return Host.ListMods(Opt)
            Case "resourcepacks", "packs"
                Return Host.ListResourcePacks(Opt)
            Case "shaderpacks", "shaders"
                Return Host.ListShaderPacks(Opt)
        End Select
        Return New List(Of PCL.PluginContentEntry)
    End Function

    ''' <summary>列 mods / resourcepacks / shaderpacks（mods.read，走宿主 v7.3 接口）。</summary>
    Private Function ModsListText(ArgsJson As String) As String
        Dim Kind As String = If(JsonStringField(ArgsJson, "kind"), "mods").Trim().ToLowerInvariant()
        Dim InstN As String = If(JsonStringField(ArgsJson, "instance"), "").Trim()
        Dim Opt As String = If(InstN.Length = 0, Nothing, InstN)
        Dim Items As List(Of PCL.PluginContentEntry) = Nothing
        Select Case Kind
            Case "mods"
                Items = Host.ListMods(Opt)
            Case "resourcepacks", "packs"
                Kind = "resourcepacks"
                Items = Host.ListResourcePacks(Opt)
            Case "shaderpacks", "shaders"
                Kind = "shaderpacks"
                Items = Host.ListShaderPacks(Opt)
            Case Else
                Return "kind 只能是 mods / resourcepacks / shaderpacks。"
        End Select
        If Items Is Nothing Then Items = New List(Of PCL.PluginContentEntry)
        If Items.Count = 0 Then Return "当前实例的 " & Kind & " 目录是空的（或没有选中实例）。"
        Dim Sb As New StringBuilder()
        Sb.Append(Kind).Append("：共 ").Append(Items.Count).Append(" 项")
        Dim N As Integer = 0
        For Each It In Items
            If N >= 200 Then
                Sb.Append(Environment.NewLine).Append("…（还有 ").Append(Items.Count - N).Append(" 项）")
                Exit For
            End If
            Sb.Append(Environment.NewLine).Append(If(It.Enabled, "[启用] ", "[禁用] ")).Append(It.FileName)
            If Not String.IsNullOrWhiteSpace(It.Name) AndAlso It.Name <> It.FileName Then
                Sb.Append("（").Append(It.Name).Append("）")
            End If
            N += 1
        Next
        If Host IsNot Nothing Then Host.LogInfo($"AI 工具 list {Kind} → {Items.Count} 项")
        Return Sb.ToString()
    End Function

    ''' <summary>启用/禁用 mods 等文件（mods.write；只改文件名后缀 .disabled，可逆）。</summary>
    Private Function ToolSetMod(ArgsJson As String) As String
        Dim Fn As String = If(JsonStringField(ArgsJson, "file"), "").Trim()
        If Fn.Length = 0 Then Return "缺少 file 参数（用 list_mods / list_resourcepacks 返回的 FileName，不要带 .disabled）。"
        Dim En As Boolean? = JsonBoolField(ArgsJson, "enabled")
        If En Is Nothing Then Return "缺少 enabled 参数（true 启用 / false 禁用）。"
        Dim Kind As String = If(JsonStringField(ArgsJson, "kind"), "mods").Trim().ToLowerInvariant()
        Dim InstN As String = If(JsonStringField(ArgsJson, "instance"), "").Trim()
        Dim Opt As String = If(InstN.Length = 0, Nothing, InstN)
        Dim Ok As Boolean
        Select Case Kind
            Case "mods"
                Ok = Host.SetModEnabled(Fn, En.Value, Opt)
            Case "resourcepacks", "packs"
                Ok = Host.SetResourcePackEnabled(Fn, En.Value, Opt)
            Case "shaderpacks", "shaders"
                Ok = Host.SetShaderPackEnabled(Fn, En.Value, Opt)
            Case Else
                Return "kind 只能是 mods / resourcepacks / shaderpacks。"
        End Select
        If Not Ok Then Return "宿主返回 False（可能文件不存在，或已有同名禁用副本，接口不会覆盖）。"
        Host.LogInfo("AI 工具 set_mod_enabled：" & Fn & " → " & If(En.Value, "启用", "禁用"))
        ' 复核：宿主对不存在的文件也会返回 True，必须回读列表确认
        Dim After As List(Of PCL.PluginContentEntry) = ContentsOf(Kind, Opt)
        Dim HitR As PCL.PluginContentEntry = Nothing
        For Each It In After
            If String.Equals(It.FileName, Fn, StringComparison.OrdinalIgnoreCase) Then
                HitR = It
                Exit For
            End If
        Next
        If HitR Is Nothing Then
            Return "宿主返回成功，但 " & Kind & " 列表里没有「" & Fn & "」——文件名可能不对（用 list_mods 确认真实 FileName，不要带 .disabled）。"
        End If
        If HitR.Enabled <> En.Value Then
            Return "已调用宿主，但复核结果不一致：现在 " & Fn & " 是" & If(HitR.Enabled, "启用", "禁用") & " 状态。"
        End If
        Return "已" & If(En.Value, "启用", "禁用") & "：" & Fn & "（列表复核通过）"
    End Function

    ''' <summary>
    ''' 只读游戏日志（logs.read）。启动器自己的日志是留给开发者排查的，不对模型开放。
    ''' 走宿主 ListLogs/ReadLog，只能读当前实例的固定路径，不接受任意路径。
    ''' </summary>
    Private Function ToolReadLog(ArgsJson As String) As String
        Dim N As Integer = JsonIntField(ArgsJson, "lines", 60)
        If N < 10 Then N = 10
        If N > 200 Then N = 200
        Return GameLogText(N)
    End Function

    ''' <summary>读当前实例的游戏日志（宿主 API，固定路径，不接受任意路径）。</summary>
    Private Function GameLogText(N As Integer) As String
        Try
            Dim L = Host.ListLogs()
            If L Is Nothing OrElse L.Count = 0 Then Return "宿主没返回任何游戏日志（当前可能没有选中实例，或这个实例还没启动过）。"
            Dim Target = Nothing
            For Each F In L
                If Target Is Nothing OrElse F.ModifiedAt > Target.ModifiedAt Then Target = F
            Next
            If Target Is Nothing Then Return "宿主没返回任何游戏日志。"
            Dim Txt As String = Host.ReadLog(Target.Path, N)
            If String.IsNullOrWhiteSpace(Txt) Then Return "游戏日志是空的：" & Target.Name
            Host.LogInfo($"AI 工具 read_log（game）→ {Target.Name}，{N} 行")
            Return "文件：" & Target.Name & "（游戏日志，最多 " & N & " 行）" & Environment.NewLine & Txt
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "读游戏日志失败：" & T.Message
        End Try
    End Function

    ''' <summary>唯一允许的写操作：只能写 notes 目录，白名单文件名，64 KB 上限。</summary>
    Private Function ToolWriteNote(ArgsJson As String) As String
        Dim Nm As String = If(JsonStringField(ArgsJson, "name"), "").Trim()
        Dim Tx As String = If(JsonStringField(ArgsJson, "text"), "")
        If Not SafeNoteName(Nm) Then
            Return "拒绝：文件名只能用字母/数字/下划线/连字符/中文，扩展名必须是 .md 或 .txt，长度 5–48。"
        End If
        If Tx.Length > 65536 Then Return "拒绝：内容超过 64 KB 上限（当前 " & Tx.Length & " 字符）。"
        Try
            Dim Dir As String = NotesDir()
            Dim DirFull As String = Path.GetFullPath(Dir)
            Dim Full As String = Path.GetFullPath(Path.Combine(DirFull, Nm))
            If Not Full.StartsWith(DirFull, StringComparison.OrdinalIgnoreCase) Then
                Return "拒绝：目标路径不在 notes 目录内。"
            End If
            Directory.CreateDirectory(DirFull)
            File.WriteAllText(Full, Tx, New UTF8Encoding(False))
            If Host IsNot Nothing Then Host.LogInfo("AI 写入笔记：" & Nm & "（" & Tx.Length & " 字符）")
            Return "已写入 notes\" & Nm & "（" & Tx.Length & " 字符）"
        Catch ex As Exception
            Dim T As Exception = If(ex.InnerException IsNot Nothing, ex.InnerException, ex)
            Return "写笔记失败：" & T.Message
        End Try
    End Function

    ''' <summary>只允许 http/https，避免 file: 或自定义协议被用来拉起本地程序。</summary>
    Private Function ToolOpenUrl(ArgsJson As String) As String
        Dim U As String = If(JsonStringField(ArgsJson, "url"), "").Trim()
        If U.Length = 0 Then Return "缺少 url 参数。"
        If U.Length > 500 Then Return "拒绝：URL 太长。"
        For Each C In U
            If Char.IsWhiteSpace(C) Then Return "拒绝：URL 里不能有空格或换行。"
        Next
        Dim Low As String = U.ToLowerInvariant()
        If Not (Low.StartsWith("https://") OrElse Low.StartsWith("http://")) Then
            Return "拒绝：只允许 http/https 链接。"
        End If
        If Low.Contains("file:") OrElse Low.StartsWith("javascript:") OrElse Low.StartsWith("data:") Then
            Return "拒绝：不允许该协议。"
        End If
        Host.OpenUrl(U)
        Return "已用系统浏览器打开：" & U
    End Function

    ''' <summary>实例列表的纯文本版（给模型 / 给 /instances）。</summary>
    Private Function InstanceListText() As String
        Dim All = AllInstances()
        If All.Count = 0 Then Return "没读到游戏实例。先在「版本选择」里添加或下载一个版本。"
        Dim Sb As New StringBuilder()
        Sb.Append("共 ").Append(All.Count).Append(" 个实例：")
        Dim N As Integer = 0
        For Each E In All
            If N >= 200 Then
                Sb.Append("…（还有 ").Append(All.Count - N).Append(" 个）")
                Exit For
            End If
            If N > 0 Then Sb.Append("；")
            If E.IsSelected Then Sb.Append("[当前] ")
            Sb.Append(E.Name)
            N += 1
        Next
        Return Sb.ToString()
    End Function
End Class

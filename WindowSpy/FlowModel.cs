using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowSpy
{
    // ===================== 流程工坊：模型 + 全中文节点目录 =====================

    public enum FlowParamType { Text, Int, Double, Bool, Coord, Rect, Combo, Code }

    public class FlowParamDef
    {
        public string Key = "";
        public string Label = "";
        public FlowParamType Type = FlowParamType.Text;
        public string Default = "";
        public string[] Options = Array.Empty<string>();
        public string Tip = "";
        public int Lines = 4;       // Code 类型的建议显示行数
    }

    /// <summary>输出端口：Port="" 表示默认出口</summary>
    public class FlowPortDef
    {
        public string Port = "";
        public string Label = "→";
        public FlowPortDef() { }
        public FlowPortDef(string port, string label) { Port = port; Label = label; }
    }

    public class FlowNodeDef
    {
        public string Type = "";
        public string Category = "";
        public string Title = "";
        public string Color = "#6B7280";
        public bool NoInput;                                   // 开始节点无入口
        public bool NoOutput;                                  // 结束节点无出口
        public List<FlowParamDef> Params = new();
        public List<FlowPortDef> Outputs = new() { new FlowPortDef("", "→") };
    }

    public class FlowNode
    {
        public string Id = "";
        public string Type = "";
        public string Alias = "";                              // 用户备注名（可空）
        public double X, Y;
        public Dictionary<string, string> Params = new();
        public bool Disabled;                                 // 屏蔽/灰化：运行时跳过，沿默认出口继续

        public string Get(string key, string def = "")
            => Params.TryGetValue(key, out var v) ? v : def;
    }

    public class FlowEdge
    {
        public string Id = "";
        public string From = "";
        public string Port = "";
        public string To = "";
    }

    public class FlowGraph
    {
        public string Name = "我的流程";
        public List<FlowNode> Nodes = new();
        public List<FlowEdge> Edges = new();
        private int _seq;

        public string NewId(string prefix)
        {
            _seq++;
            return $"{prefix}_{_seq}_{Guid.NewGuid().ToString("N").Substring(0, 4)}";
        }

        public FlowNode? Node(string id) => Nodes.Find(n => n.Id == id);
        public FlowEdge? OutEdge(string nodeId, string port)
            => Edges.Find(e => e.From == nodeId && e.Port == port);
        public List<FlowEdge> InEdges(string nodeId) => Edges.FindAll(e => e.To == nodeId);

        public FlowNode AddNode(string type, double x, double y)
        {
            var def = FlowCatalog.Get(type);
            var n = new FlowNode
            {
                Id = NewId("n"), Type = type, X = x, Y = y,
                Alias = ""
            };
            foreach (var p in def.Params) n.Params[p.Key] = p.Default;
            Nodes.Add(n);
            return n;
        }

        public void RemoveNode(string id)
        {
            Nodes.RemoveAll(n => n.Id == id);
            Edges.RemoveAll(e => e.From == id || e.To == id);
        }

        // —— 持久化 ——（模型数据均为 public 字段，必须 IncludeFields，否则存出来是空 {}）
        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            IncludeFields = true
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpt);

        public static FlowGraph FromJson(string json)
        {
            var g = JsonSerializer.Deserialize<FlowGraph>(json, JsonOpt) ?? new FlowGraph();
            g.Nodes ??= new List<FlowNode>();
            g.Edges ??= new List<FlowEdge>();
            int max = 0;
            foreach (var n in g.Nodes)
            {
                n.Params ??= new Dictionary<string, string>();
                foreach (var part in n.Id.Split('_'))
                    if (int.TryParse(part, out int v) && v > max) max = v;
            }
            g._seq = max;
            return g;
        }
    }

    /// <summary>全中文节点目录：六类（流程/运动控制/视觉识别/买卖控制/逻辑判断/AI风控）</summary>
    public static class FlowCatalog
    {
        public const string CatFlow = "流程";
        public const string CatMotion = "运动控制";
        public const string CatVision = "视觉识别";
        public const string CatTrade = "买卖控制";
        public const string CatLogic = "逻辑判断";
        public const string CatAi = "AI风控";
        public const string CatScript = "脚本扩展";
        public const string CatModule = "我的模块";

        private static readonly List<FlowNodeDef> _defs = new();
        public static IReadOnlyList<FlowNodeDef> Defs => _defs;

        static FlowCatalog()
        {
            // —————— 流程 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "start", Category = CatFlow, Title = "开始", Color = "#58E07D", NoInput = true,
                Params = new()
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "end", Category = CatFlow, Title = "结束", Color = "#FF4D55", NoOutput = true,
                Params = new()
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "delay", Category = CatFlow, Title = "延时等待", Color = "#6B7280",
                Params = new()
                {
                    new() { Key = "sec", Label = "等待秒数", Type = FlowParamType.Double, Default = "1" },
                    new() { Key = "jitter", Label = "随机±秒", Type = FlowParamType.Double, Default = "0", Tip = "拟人：实际等待在基准值上下随机" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "note", Category = CatFlow, Title = "备注", Color = "#6B7280",
                Params = new()
                {
                    new() { Key = "text", Label = "备注内容(不执行)", Type = FlowParamType.Text, Default = "在这里写说明" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "log_msg", Category = CatFlow, Title = "打印日志", Color = "#6B7280",
                Params = new()
                {
                    new() { Key = "text", Label = "日志内容(支持{变量})", Type = FlowParamType.Text, Default = "走到这里了" },
                    new() { Key = "level", Label = "日志级别", Type = FlowParamType.Combo, Default = "普通",
                        Options = new[] { "普通", "动作", "等待", "警告", "错误" } }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "beep", Category = CatFlow, Title = "声音提醒", Color = "#6B7280",
                Params = new()
                {
                    new() { Key = "times", Label = "响几声", Type = FlowParamType.Int, Default = "2", Tip = "需要人工介入/出结果时提醒" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "stop_here", Category = CatFlow, Title = "主动停止流程", Color = "#FF4D55", NoOutput = true,
                Params = new()
                {
                    new() { Key = "text", Label = "停止原因(写入日志)", Type = FlowParamType.Text, Default = "流程到此结束" }
                }
            });

            // —————— 运动控制 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "click", Category = CatMotion, Title = "点击坐标", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "pos", Label = "点击位置", Type = FlowParamType.Coord, Tip = "游戏窗口相对坐标" },
                    new() { Key = "dwell", Label = "按下停留ms", Type = FlowParamType.Int, Default = "80" },
                    new() { Key = "jitter", Label = "随机偏移px", Type = FlowParamType.Int, Default = "3" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "dblclick", Category = CatMotion, Title = "双击坐标", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "pos", Label = "双击位置", Type = FlowParamType.Coord },
                    new() { Key = "dwell", Label = "间隔ms", Type = FlowParamType.Int, Default = "60" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "keypress", Category = CatMotion, Title = "按键", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "key", Label = "按键", Type = FlowParamType.Combo, Default = "Esc",
                        Options = new[] { "Esc", "回车", "空格", "Tab", "F5", "F12", "上", "下", "左", "右" } },
                    new() { Key = "vk", Label = "或自定义VK码", Type = FlowParamType.Text, Default = "", Tip = "填了数字优先用VK码，如 27=Esc 13=回车" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "input", Category = CatMotion, Title = "输入文本", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "text", Label = "文本内容", Type = FlowParamType.Text, Tip = "可用 {变量名} 拼接，如 {选品品名}；自动全选覆盖后粘贴" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "bringfront", Category = CatMotion, Title = "窗口置顶", Color = "#FF8A2A",
                Params = new()
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "repeatclick", Category = CatMotion, Title = "连点器(数量+/-)", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "pos", Label = "目标按钮", Type = FlowParamType.Coord },
                    new() { Key = "times", Label = "连点次数", Type = FlowParamType.Int, Default = "1" },
                    new() { Key = "interval", Label = "间隔ms", Type = FlowParamType.Int, Default = "40" },
                    new() { Key = "dwell", Label = "每次停留ms", Type = FlowParamType.Int, Default = "15" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "mouse_move", Category = CatMotion, Title = "鼠标移动到", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "pos", Label = "目标位置", Type = FlowParamType.Coord }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "left_down", Category = CatMotion, Title = "左键按下", Color = "#FF8A2A",
                Params = new() { new() { Key = "pos", Label = "按下位置", Type = FlowParamType.Coord } }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "left_up", Category = CatMotion, Title = "左键弹起", Color = "#FF8A2A",
                Params = new() { new() { Key = "pos", Label = "弹起位置", Type = FlowParamType.Coord } }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "right_click", Category = CatMotion, Title = "右键单击", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "pos", Label = "点击位置", Type = FlowParamType.Coord },
                    new() { Key = "dwell", Label = "按下停留ms", Type = FlowParamType.Int, Default = "80" },
                    new() { Key = "jitter", Label = "随机偏移px", Type = FlowParamType.Int, Default = "3" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "right_down", Category = CatMotion, Title = "右键按下", Color = "#FF8A2A",
                Params = new() { new() { Key = "pos", Label = "按下位置", Type = FlowParamType.Coord } }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "right_up", Category = CatMotion, Title = "右键弹起", Color = "#FF8A2A",
                Params = new() { new() { Key = "pos", Label = "弹起位置", Type = FlowParamType.Coord } }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "middle_click", Category = CatMotion, Title = "中键单击", Color = "#FF8A2A",
                Params = new() { new() { Key = "pos", Label = "点击位置", Type = FlowParamType.Coord } }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "drag", Category = CatMotion, Title = "鼠标拖拽", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "from", Label = "起点", Type = FlowParamType.Coord },
                    new() { Key = "to", Label = "终点", Type = FlowParamType.Coord },
                    new() { Key = "duration", Label = "拖拽耗时ms", Type = FlowParamType.Int, Default = "400", Tip = "中间分步移动，模拟人手拖" },
                    new() { Key = "button", Label = "按键", Type = FlowParamType.Combo, Default = "左键", Options = new[] { "左键", "右键" } }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "wheel", Category = CatMotion, Title = "鼠标滚轮", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "pos", Label = "滚动位置", Type = FlowParamType.Coord, Tip = "先移动到该位置再滚" },
                    new() { Key = "ticks", Label = "滚动格数(正上负下)", Type = FlowParamType.Int, Default = "3" },
                    new() { Key = "stepms", Label = "每格间隔ms", Type = FlowParamType.Int, Default = "60" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "key_down", Category = CatMotion, Title = "键盘按下", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "key", Label = "按键", Type = FlowParamType.Combo, Default = "Ctrl",
                        Options = new[] { "Ctrl", "Shift", "Alt", "Win", "Esc", "回车", "空格", "Tab", "A", "B", "C", "D", "E", "F", "V", "Z", "X", "Y", "F5", "F12", "上", "下", "左", "右" } },
                    new() { Key = "vk", Label = "或自定义VK码", Type = FlowParamType.Text, Default = "" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "key_up", Category = CatMotion, Title = "键盘弹起", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "key", Label = "按键", Type = FlowParamType.Combo, Default = "Ctrl",
                        Options = new[] { "Ctrl", "Shift", "Alt", "Win", "Esc", "回车", "空格", "Tab", "A", "B", "C", "D", "E", "F", "V", "Z", "X", "Y", "F5", "F12", "上", "下", "左", "右" } },
                    new() { Key = "vk", Label = "或自定义VK码", Type = FlowParamType.Text, Default = "" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "key_combo", Category = CatMotion, Title = "组合键", Color = "#FF8A2A",
                Params = new()
                {
                    new() { Key = "combo", Label = "组合键", Type = FlowParamType.Text, Default = "Ctrl+A",
                        Tip = "如 Ctrl+A、Ctrl+Shift+V、Alt+Tab、Win+D；用+连接" }
                }
            });

            // —————— 视觉识别 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "ocr_region", Category = CatVision, Title = "区域OCR识别", Color = "#4DA3FF",
                Params = new()
                {
                    new() { Key = "rect", Label = "识别区域", Type = FlowParamType.Rect },
                    new() { Key = "num", Label = "仅保留数字", Type = FlowParamType.Bool, Default = "false" },
                    new() { Key = "var", Label = "结果存入变量", Type = FlowParamType.Text, Default = "识别结果" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "ocr_screen", Category = CatVision, Title = "全屏OCR识别", Color = "#4DA3FF",
                Params = new()
                {
                    new() { Key = "var", Label = "结果存入变量", Type = FlowParamType.Text, Default = "全屏文本" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "text_contains", Category = CatVision, Title = "文本含关键词", Color = "#4DA3FF",
                Outputs = new() { new("found", "找到"), new("miss", "没找到") },
                Params = new()
                {
                    new() { Key = "var", Label = "来源变量", Type = FlowParamType.Text, Default = "全屏文本" },
                    new() { Key = "words", Label = "关键词(逗号分隔，任一命中)", Type = FlowParamType.Text, Default = "领取,哈弗币" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "read_wallet", Category = CatVision, Title = "读哈弗币余额", Color = "#4DA3FF",
                Params = new()
                {
                    new() { Key = "rect", Label = "余额区域(含K/M字母)", Type = FlowParamType.Rect },
                    new() { Key = "var", Label = "结果存入变量", Type = FlowParamType.Text, Default = "余额" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "find_template", Category = CatVision, Title = "模板匹配(找图)", Color = "#4DA3FF",
                Outputs = new() { new("found", "找到"), new("miss", "没找到") },
                Params = new()
                {
                    new() { Key = "tpl", Label = "模板(点右侧标定)", Type = FlowParamType.Combo, Default = "", Tip = "在属性面板点「标定模板」框选截图" },
                    new() { Key = "region", Label = "搜索区域(空=全屏)", Type = FlowParamType.Rect },
                    new() { Key = "threshold", Label = "相似度0~1", Type = FlowParamType.Double, Default = "0.85" },
                    new() { Key = "prefix", Label = "输出变量前缀", Type = FlowParamType.Text, Default = "找图",
                        Tip = "输出：找图X/找图Y(窗口相对中心坐标)、找图相似度" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "click_template", Category = CatVision, Title = "找到模板并点击", Color = "#4DA3FF",
                Outputs = new() { new("found", "找到并点击"), new("miss", "没找到") },
                Params = new()
                {
                    new() { Key = "tpl", Label = "模板", Type = FlowParamType.Combo, Default = "" },
                    new() { Key = "region", Label = "搜索区域(空=全屏)", Type = FlowParamType.Rect },
                    new() { Key = "threshold", Label = "相似度0~1", Type = FlowParamType.Double, Default = "0.85" },
                    new() { Key = "button", Label = "鼠标按键", Type = FlowParamType.Combo, Default = "左键", Options = new[] { "左键", "右键", "中键" } },
                    new() { Key = "dwell", Label = "停留ms", Type = FlowParamType.Int, Default = "80" },
                    new() { Key = "offsetx", Label = "点击偏移X", Type = FlowParamType.Int, Default = "0", Tip = "相对模板中心偏移" },
                    new() { Key = "offsety", Label = "点击偏移Y", Type = FlowParamType.Int, Default = "0" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "wait_template", Category = CatVision, Title = "等待模板出现", Color = "#4DA3FF",
                Outputs = new() { new("found", "出现了"), new("timeout", "超时") },
                Params = new()
                {
                    new() { Key = "tpl", Label = "模板", Type = FlowParamType.Combo, Default = "" },
                    new() { Key = "region", Label = "搜索区域(空=全屏)", Type = FlowParamType.Rect },
                    new() { Key = "threshold", Label = "相似度0~1", Type = FlowParamType.Double, Default = "0.85" },
                    new() { Key = "timeout", Label = "超时秒", Type = FlowParamType.Int, Default = "10" },
                    new() { Key = "interval", Label = "检测间隔ms", Type = FlowParamType.Int, Default = "500" },
                    new() { Key = "prefix", Label = "坐标输出前缀", Type = FlowParamType.Text, Default = "等图" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "wait_gone", Category = CatVision, Title = "等待模板消失", Color = "#4DA3FF",
                Outputs = new() { new("gone", "消失了"), new("timeout", "超时") },
                Params = new()
                {
                    new() { Key = "tpl", Label = "模板", Type = FlowParamType.Combo, Default = "" },
                    new() { Key = "region", Label = "搜索区域(空=全屏)", Type = FlowParamType.Rect },
                    new() { Key = "threshold", Label = "相似度低于该值视为消失", Type = FlowParamType.Double, Default = "0.85" },
                    new() { Key = "timeout", Label = "超时秒", Type = FlowParamType.Int, Default = "10" },
                    new() { Key = "interval", Label = "检测间隔ms", Type = FlowParamType.Int, Default = "500" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "wait_text", Category = CatVision, Title = "等待屏幕文字", Color = "#4DA3FF",
                Outputs = new() { new("found", "出现了"), new("timeout", "超时") },
                Params = new()
                {
                    new() { Key = "words", Label = "等待的关键词(逗号分隔)", Type = FlowParamType.Text, Default = "上架,出售",
                        Tip = "轮询全屏OCR，任一关键词出现即继续；适合等弹窗/加载完成" },
                    new() { Key = "timeout", Label = "超时秒", Type = FlowParamType.Int, Default = "10" },
                    new() { Key = "interval", Label = "检测间隔ms", Type = FlowParamType.Int, Default = "600" }
                }
            });

            // —————— 买卖控制 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "enter_trade", Category = CatTrade, Title = "进入交易行", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "pos", Label = "交易行入口", Type = FlowParamType.Coord }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "claim_mail", Category = CatTrade, Title = "领取邮件哈弗币", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "mail", Label = "邮箱入口", Type = FlowParamType.Coord },
                    new() { Key = "claim", Label = "一键领取", Type = FlowParamType.Coord },
                    new() { Key = "close", Label = "关闭按钮(可空=Esc)", Type = FlowParamType.Coord },
                    new() { Key = "words", Label = "领取关键词", Type = FlowParamType.Text, Default = "领取,哈弗币,附件" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "fetch_quotes", Category = CatTrade, Title = "拉取全部行情", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "url", Label = "行情地址(留空默认)", Type = FlowParamType.Text, Default = "" },
                    new() { Key = "var", Label = "结果存入变量", Type = FlowParamType.Text, Default = "行情列表" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "pick", Category = CatTrade, Title = "自动选品", Color = "#58E07D",
                Outputs = new() { new("ok", "选到了"), new("miss", "没有合适的") },
                Params = new()
                {
                    new() { Key = "quotes", Label = "行情列表变量", Type = FlowParamType.Text, Default = "行情列表" },
                    new() { Key = "strategy", Label = "选品策略", Type = FlowParamType.Combo, Default = "SMART",
                        Options = new[] { "SMART", "PROFIT", "RISE", "DIP", "REBOUND", "RANGE" } },
                    new() { Key = "budget", Label = "预算(数字或{变量})", Type = FlowParamType.Text, Default = "{余额}", Tip = "0=不按预算；默认取余额变量" },
                    new() { Key = "minprofit", Label = "最低税后利润%", Type = FlowParamType.Double, Default = "3" },
                    new() { Key = "maxlots", Label = "份数上限", Type = FlowParamType.Int, Default = "999" },
                    new() { Key = "prefix", Label = "输出变量前缀", Type = FlowParamType.Text, Default = "选品",
                        Tip = "输出：选品品名/选品现价/选品份数/选品预期利润/选品卖出价" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "select_bullet", Category = CatTrade, Title = "搜索选中子弹", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "name", Label = "子弹名(支持{变量})", Type = FlowParamType.Text, Default = "{选品品名}" },
                    new() { Key = "box", Label = "搜索框", Type = FlowParamType.Coord },
                    new() { Key = "result", Label = "结果第一项", Type = FlowParamType.Coord }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "adjust_lots", Category = CatTrade, Title = "调整买入份数", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "minus", Label = "数量−按钮", Type = FlowParamType.Coord },
                    new() { Key = "plus", Label = "数量+按钮", Type = FlowParamType.Coord },
                    new() { Key = "reset", Label = "先点−复位次数", Type = FlowParamType.Int, Default = "20" },
                    new() { Key = "target", Label = "目标份数(数字或{变量})", Type = FlowParamType.Text, Default = "{选品份数}" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "buy", Category = CatTrade, Title = "买入", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "pos", Label = "买入按钮", Type = FlowParamType.Coord },
                    new() { Key = "settle", Label = "成交等待ms", Type = FlowParamType.Int, Default = "350" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "sell", Category = CatTrade, Title = "卖出", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "pos", Label = "卖出按钮", Type = FlowParamType.Coord },
                    new() { Key = "settle", Label = "成交等待ms", Type = FlowParamType.Int, Default = "350" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "hold_quote", Category = CatTrade, Title = "持仓行情/指标", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "quotes", Label = "行情列表变量", Type = FlowParamType.Text, Default = "行情列表" },
                    new() { Key = "name", Label = "持仓子弹名", Type = FlowParamType.Text, Default = "{选品品名}" },
                    new() { Key = "buyprice", Label = "买入价(数字或{变量})", Type = FlowParamType.Text, Default = "{选品现价}" },
                    new() { Key = "ma", Label = "均线周期", Type = FlowParamType.Int, Default = "20" },
                    new() { Key = "prefix", Label = "输出变量前缀", Type = FlowParamType.Text, Default = "持仓",
                        Tip = "输出：持仓现价/浮盈%/MA/RSI/位置/预期卖出" }
                }
            });

            // —— 出售链路（仓库→选子弹→渠道弹窗→交易行定价上架 / 军需处回收）——
            _defs.Add(new FlowNodeDef
            {
                Type = "open_warehouse", Category = CatTrade, Title = "打开仓库", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "pos", Label = "仓库入口坐标", Type = FlowParamType.Coord, Tip = "大厅/特勤处里的仓库入口" },
                    new() { Key = "settle", Label = "打开等待ms", Type = FlowParamType.Int, Default = "600" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "goto_ammo", Category = CatTrade, Title = "切到弹药分类", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "pos", Label = "弹药页签/分类坐标", Type = FlowParamType.Coord, Tip = "仓库或交易行出售页右侧物品栏的「弹药」分类" },
                    new() { Key = "settle", Label = "切换等待ms", Type = FlowParamType.Int, Default = "400" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "trade_tab", Category = CatTrade, Title = "交易行切页签", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "tab", Label = "目标页签", Type = FlowParamType.Combo, Default = "出售",
                        Options = new[] { "购买", "出售", "交易记录" } },
                    new() { Key = "pos", Label = "页签坐标", Type = FlowParamType.Coord },
                    new() { Key = "settle", Label = "切换等待ms", Type = FlowParamType.Int, Default = "500" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "sell_channel", Category = CatTrade, Title = "出售渠道弹窗", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "channel", Label = "卖给谁", Type = FlowParamType.Combo, Default = "交易行",
                        Options = new[] { "交易行", "军需处" }, Tip = "军需处回收价很低，蓝品以上子弹一般挂交易行" },
                    new() { Key = "tradePos", Label = "弹窗里「交易行」按钮", Type = FlowParamType.Coord, Tip = "坐标留空则跳过本步（从交易行出售页发起时无弹窗）" },
                    new() { Key = "merchantPos", Label = "弹窗里「军需处」按钮", Type = FlowParamType.Coord },
                    new() { Key = "wait", Label = "弹窗出现等待ms", Type = FlowParamType.Int, Default = "700" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "sell_pick_item", Category = CatTrade, Title = "选子弹出售", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "name", Label = "子弹名(支持{变量})", Type = FlowParamType.Text, Default = "{选品品名}" },
                    new() { Key = "result", Label = "物品位置(出售页右侧仓库)", Type = FlowParamType.Coord },
                    new() { Key = "box", Label = "搜索框(没有就留空)", Type = FlowParamType.Coord, Tip = "交易行出售页一般无搜索框，直接点右侧物品；有搜索框则填，会先搜索" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "set_price", Category = CatTrade, Title = "上架定价", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "mode", Label = "定价方式", Type = FlowParamType.Combo, Default = "直接输入价",
                        Options = new[] { "直接输入价", "点价格柱", "删位法+价格柱" },
                        Tip = "直接输入价：价格框填数字；价格柱：点你提前选好的档位柱；删位法：输入参考价→删末尾N位→点价格柱（秒卖常用）" },
                    new() { Key = "price", Label = "价格(数字或{变量})", Type = FlowParamType.Text, Default = "{持仓卖出价}" },
                    new() { Key = "box", Label = "价格输入框", Type = FlowParamType.Coord },
                    new() { Key = "bar", Label = "价格柱坐标", Type = FlowParamType.Coord, Tip = "5档柱状图里要点的那一档，框选时点准柱子" },
                    new() { Key = "deldigits", Label = "删位法删几位", Type = FlowParamType.Int, Default = "1" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "set_sell_qty", Category = CatTrade, Title = "调整上架数量", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "minus", Label = "数量−按钮", Type = FlowParamType.Coord },
                    new() { Key = "plus", Label = "数量+按钮", Type = FlowParamType.Coord },
                    new() { Key = "reset", Label = "先点−复位次数", Type = FlowParamType.Int, Default = "20" },
                    new() { Key = "target", Label = "目标数量(数字或{变量})", Type = FlowParamType.Text, Default = "{选品份数}" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "list_confirm", Category = CatTrade, Title = "确认上架", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "pos", Label = "上架按钮", Type = FlowParamType.Coord },
                    new() { Key = "confirm", Label = "二次确认按钮(没有留空)", Type = FlowParamType.Coord },
                    new() { Key = "settle", Label = "成交等待ms", Type = FlowParamType.Int, Default = "600" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "recycle_batch", Category = CatTrade, Title = "军需处批量回收", Color = "#58E07D",
                Params = new()
                {
                    new() { Key = "cart", Label = "仓库底部推车图标", Type = FlowParamType.Coord, Tip = "白/绿品质物资军需处回收与交易行税后价差不大，适合批量清仓" },
                    new() { Key = "selectall", Label = "全选按钮(没有留空)", Type = FlowParamType.Coord },
                    new() { Key = "recycle", Label = "回收/出售按钮", Type = FlowParamType.Coord },
                    new() { Key = "confirm", Label = "确认回收按钮(没有留空)", Type = FlowParamType.Coord },
                    new() { Key = "settle", Label = "每步等待ms", Type = FlowParamType.Int, Default = "500" }
                }
            });

            // —————— 逻辑判断 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "if", Category = CatLogic, Title = "如果…否则", Color = "#B98CFF",
                Outputs = new() { new("true", "是"), new("false", "否") },
                Params = new()
                {
                    new() { Key = "expr", Label = "条件表达式", Type = FlowParamType.Text, Default = "浮盈 > 10",
                        Tip = "示例：浮盈 > 10 ；RSI < 30 ；余额 >= 100000；识别结果 == '成功'；可用 and/or/not" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "loop_n", Category = CatLogic, Title = "循环N次", Color = "#B98CFF",
                Outputs = new() { new("body", "循环体"), new("done", "结束后") },
                Params = new()
                {
                    new() { Key = "count", Label = "循环次数", Type = FlowParamType.Int, Default = "999" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "loop_while", Category = CatLogic, Title = "条件循环", Color = "#B98CFF",
                Outputs = new() { new("body", "满足→循环体"), new("done", "不满足→") },
                Params = new()
                {
                    new() { Key = "expr", Label = "继续循环的条件", Type = FlowParamType.Text, Default = "1 == 1" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "break", Category = CatLogic, Title = "跳出循环", Color = "#B98CFF",
                Params = new()
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "set_var", Category = CatLogic, Title = "设置变量", Color = "#B98CFF",
                Params = new()
                {
                    new() { Key = "name", Label = "变量名", Type = FlowParamType.Text, Default = "新变量" },
                    new() { Key = "value", Label = "值(文本/数字/{变量})", Type = FlowParamType.Text, Default = "" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "calc", Category = CatLogic, Title = "表达式计算", Color = "#B98CFF",
                Params = new()
                {
                    new() { Key = "expr", Label = "计算表达式", Type = FlowParamType.Text, Default = "余额 * 0.5",
                        Tip = "支持 +-*/、括号、中文变量，如 (持仓现价-选品现价)*选品份数" },
                    new() { Key = "var", Label = "结果存入变量", Type = FlowParamType.Text, Default = "计算结果" }
                }
            });

            // —————— AI风控 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "ai_ask", Category = CatAi, Title = "AI决策", Color = "#FF4D55",
                Params = new()
                {
                    new() { Key = "prompt", Label = "提问内容(支持{变量})", Type = FlowParamType.Text,
                        Default = "子弹{选品品名}现价{选品现价}，预期利润{选品预期利润}%，只回答BUY或WAIT" },
                    new() { Key = "model", Label = "模型(留空默认)", Type = FlowParamType.Text, Default = "" },
                    new() { Key = "var", Label = "回复存入变量", Type = FlowParamType.Text, Default = "AI回复" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "ai_branch", Category = CatAi, Title = "AI是/否判断", Color = "#FF4D55",
                Outputs = new() { new("yes", "是"), new("no", "否") },
                Params = new()
                {
                    new() { Key = "question", Label = "只能用是/否回答的问题(支持{变量})", Type = FlowParamType.Text,
                        Default = "子弹{选品品名}现价低于均线且预期利润为正，现在适合买入吗？只回答是或否" },
                    new() { Key = "model", Label = "模型(留空默认)", Type = FlowParamType.Text, Default = "" },
                    new() { Key = "var", Label = "回复存入变量", Type = FlowParamType.Text, Default = "AI判断" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "ai_execute", Category = CatAi, Title = "AI三选一执行", Color = "#FF4D55",
                Outputs = new() { new("a", "方案A"), new("b", "方案B"), new("c", "方案C") },
                Params = new()
                {
                    new() { Key = "prompt", Label = "任务说明(支持{变量})", Type = FlowParamType.Text,
                        Default = "当前浮盈{持仓浮盈}% RSI{持仓RSI}。只回答A买入、B卖出或C等待中的一个字母" },
                    new() { Key = "kwA", Label = "方案A关键词", Type = FlowParamType.Text, Default = "A" },
                    new() { Key = "kwB", Label = "方案B关键词", Type = FlowParamType.Text, Default = "B" },
                    new() { Key = "kwC", Label = "方案C关键词", Type = FlowParamType.Text, Default = "C" },
                    new() { Key = "model", Label = "模型(留空默认)", Type = FlowParamType.Text, Default = "" },
                    new() { Key = "var", Label = "回复存入变量", Type = FlowParamType.Text, Default = "AI执行" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "ban_check", Category = CatAi, Title = "封禁检测", Color = "#FF4D55",
                Outputs = new() { new("safe", "安全"), new("hit", "命中→") },
                Params = new()
                {
                    new() { Key = "words", Label = "封禁关键词(留空用默认)", Type = FlowParamType.Text,
                        Default = "封禁,封号,封停,冻结,异常,违规,惩罚,限制,警告" }
                }
            });

            // —————— 脚本扩展 ——————
            _defs.Add(new FlowNodeDef
            {
                Type = "lua_script", Category = CatScript, Title = "Lua脚本", Color = "#3FD6C9",
                Outputs = new() { new("", "→"), new("error", "脚本报错") },
                Params = new()
                {
                    new() { Key = "code", Label = "Lua 脚本代码", Type = FlowParamType.Code,
                        Lines = 8,
                        Default = "-- 运行时可用全局：vars (table), log(level,msg)\n-- 返回值会自动写回 vars\n\n-- 示例：自定义选品策略\nlocal m = vars[\"行情列表\"]\nlocal best = nil\nlocal bestProfit = -999\nif m then\n  for k, v in ipairs(m) do\n    if v.profit and v.profit > bestProfit then\n      bestProfit = v.profit\n      best = v\n    end\n  end\nend\nif best then\n  vars[\"选品品名\"] = best.name\n  vars[\"选品现价\"] = best.price\n  vars[\"选品预期利润\"] = bestProfit\n  vars[\"选品份数\"] = 1\nend",
                        Tip = "MoonSharp 0.9.8 Lua 5.2 兼容：全局 vars(table)=流程变量、log(msg)=写日志；脚本 return 值写回 vars；沙箱禁用 IO/OS/debug" },
                    new() { Key = "timeout", Label = "超时(秒)", Type = FlowParamType.Int, Default = "10" }
                }
            });
            _defs.Add(new FlowNodeDef
            {
                Type = "python_script", Category = CatScript, Title = "Python脚本", Color = "#3776AB",
                Outputs = new() { new("", "→"), new("error", "脚本报错") },
                Params = new()
                {
                    new() { Key = "code", Label = "Python 脚本代码", Type = FlowParamType.Code,
                        Lines = 8,
                        Default = "# 运行时环境：import json, os, sys；通过 __input__ 读 vars，通过 __output__ 写回 vars\n# 示例：数据分析/统计\nimport json, math\n\ninp = json.loads(os.environ.get(\"__INPUT__\", \"{}\"))\nvars = inp.get(\"vars\", {})\n\n# 写统计\nprices = vars.get(\"行情列表\", [])\nif prices:\n    avg = sum(p[\"price\"] for p in prices) / len(prices)\n    vars[\"均价\"] = round(avg, 2)\n    vars[\"行情数量\"] = len(prices)\n\nprint(\"Python 分析完成\", file=sys.stderr)  # 写到日志\nprint(json.dumps({\"vars\": vars}))  # stdout 解析为输出",
                        Tip = "通过外部 python.exe 执行。stdin 收到 JSON{vars:{...}}；stdout 输出 JSON{vars:{...}} 写回。stderr 写入流程日志。" },
                    new() { Key = "python", Label = "python 路径", Type = FlowParamType.Text, Default = "python",
                        Tip = "留 python 自动查 PATH；也可填完整路径如 C:\\Python312\\python.exe" },
                    new() { Key = "timeout", Label = "超时(秒)", Type = FlowParamType.Int, Default = "30" }
                }
            });

            // —————— 我的模块（子流程折叠卡片；具体模块由工具箱动态注入，这里只注册卡片本身）——————
            _defs.Add(new FlowNodeDef
            {
                Type = "subflow", Category = CatModule, Title = "子流程模块", Color = "#B98CFF",
                Params = new()
                {
                    new() { Key = "mod", Label = "模块文件(内部)", Type = FlowParamType.Text, Default = "" },
                    new() { Key = "modname", Label = "模块名称(内部)", Type = FlowParamType.Text, Default = "" }
                }
            });

            // —— 统一注入「目标窗口」参数：运动/视觉/买卖三类节点都可选择操作顶部绑定的窗口A或窗口B（双开倒卖）——
            // 注意键名不能用 target（adjust_lots/set_sell_qty 已用 target 表示份数）
            // 例外：纯网络/内存节点不碰游戏窗口句柄，绝不能要求绑定（否则没绑窗口时拉行情/选品直接报错）
            var noWindowTypes = new HashSet<string> { "fetch_quotes", "pick", "hold_quote" };
            foreach (var d in _defs)
            {
                if (d.Category != CatMotion && d.Category != CatVision && d.Category != CatTrade) continue;
                if (noWindowTypes.Contains(d.Type)) continue;
                d.Params.Insert(0, new FlowParamDef
                {
                    Key = "wintarget", Label = "目标窗口", Type = FlowParamType.Combo, Default = "A",
                    Options = new[] { "A", "B" },
                    Tip = "该节点操作哪个已绑定的游戏窗口（双开：A操作窗口A，B操作窗口B）"
                });
            }
        }

        public static FlowNodeDef Get(string type)
            => _defs.Find(d => d.Type == type) ?? new FlowNodeDef { Type = type, Title = type };

        /// <summary>节点卡片副标题（参数摘要）</summary>
        public static string Summary(FlowNode n)
        {
            string S(string k) => n.Get(k);
            string s = n.Type switch
            {
                "delay" => $"{S("sec")}秒",
                "note" => Trunc(S("text"), 14),
                "log_msg" => Trunc(S("text"), 14),
                "beep" => $"响{S("times")}声",
                "stop_here" => Trunc(S("text"), 12),
                "click" => $"点 {S("pos")}",
                "dblclick" => $"双击 {S("pos")}",
                "mouse_move" => $"移到 {S("pos")}",
                "left_down" => $"左键按下 {S("pos")}",
                "left_up" => $"左键弹起 {S("pos")}",
                "right_click" => $"右键点 {S("pos")}",
                "right_down" => $"右键按下 {S("pos")}",
                "right_up" => $"右键弹起 {S("pos")}",
                "middle_click" => $"中键点 {S("pos")}",
                "drag" => $"{S("from")}→{S("to")}",
                "wheel" => $"滚{S("ticks")}格",
                "key_down" => $"按下 {KeyName(S("key"), S("vk"))}",
                "key_up" => $"弹起 {KeyName(S("key"), S("vk"))}",
                "key_combo" => S("combo"),
                "keypress" => string.IsNullOrWhiteSpace(S("vk")) ? S("key") : $"VK {S("vk")}",
                "input" => Trunc(S("text"), 16),
                "repeatclick" => $"连点{S("times")}次",
                "ocr_region" => $"→{S("var")}",
                "ocr_screen" => $"→{S("var")}",
                "text_contains" => $"[{S("words")}]",
                "read_wallet" => $"→{S("var")}",
                "find_template" => $"[{Trunc(S("tpl"), 8)}]→{S("prefix")}*",
                "click_template" => $"点[{Trunc(S("tpl"), 8)}]",
                "wait_template" => $"等[{Trunc(S("tpl"), 8)}]{S("timeout")}s",
                "wait_gone" => $"等消失[{Trunc(S("tpl"), 8)}]",
                "wait_text" => $"等文字「{Trunc(S("words"), 10)}」{S("timeout")}s",
                "enter_trade" => "进入交易行",
                "claim_mail" => "一键领取",
                "fetch_quotes" => $"→{S("var")}",
                "pick" => $"{S("strategy")} →{S("prefix")}*",
                "select_bullet" => Trunc(S("name"), 14),
                "adjust_lots" => $"{S("reset")}复位→{S("target")}份",
                "buy" => "执行买入",
                "sell" => "执行卖出",
                "hold_quote" => $"→{S("prefix")}*",
                "open_warehouse" => "打开仓库",
                "goto_ammo" => "弹药分类",
                "trade_tab" => $"切到{S("tab")}页签",
                "sell_channel" => $"卖给{S("channel")}",
                "sell_pick_item" => Trunc(S("name"), 12),
                "set_price" => $"{S("mode")} {Trunc(S("price"), 8)}",
                "set_sell_qty" => $"上架{S("target")}份",
                "list_confirm" => "确认上架",
                "recycle_batch" => "军需处回收",
                "if" => Trunc(S("expr"), 16),
                "loop_n" => $"×{S("count")}",
                "loop_while" => Trunc(S("expr"), 14),
                "break" => "跳出",
                "set_var" => $"{S("name")}={Trunc(S("value"), 10)}",
                "calc" => $"{S("var")}={Trunc(S("expr"), 10)}",
                "ai_ask" => $"→{S("var")}",
                "ai_branch" => "AI答是/否",
                "ai_execute" => "AI选A/B/C",
                "ban_check" => "全屏关键词",
                "lua_script" => "Lua " + FirstLine(S("code"), 24),
                "python_script" => "Py " + FirstLine(S("code"), 24),
                "subflow" => "🧩 " + (string.IsNullOrWhiteSpace(S("modname")) ? "未选择模块" : Trunc(S("modname"), 14)),
                _ => ""
            };
            // 目标窗口B：卡片摘要加 B· 前缀，画布上一眼区分双开操作
            if (s.Length > 0 && string.Equals((n.Get("wintarget") ?? "A").Trim(), "B", StringComparison.OrdinalIgnoreCase))
                s = "B·" + s;
            return s;
        }

        private static string Trunc(string s, int n)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        private static string FirstLine(string s, int n)
        {
            s = (s ?? "").Trim();
            var i = s.IndexOf('\n');
            if (i >= 0) s = s.Substring(0, i);
            return Trunc(s, n);
        }

        private static string KeyName(string key, string vk)
            => string.IsNullOrWhiteSpace(vk) ? (key ?? "") : $"VK{vk}";
    }

    // ===================== 轻量表达式求值（中文变量/比较/逻辑/算术） =====================
    public static class FlowExpr
    {
        /// <summary>求值；失败 ok=false。支持：数字、'字符串'、中文变量、+ - * /、&gt; &lt; &gt;= &lt;= == !=、and or not、括号</summary>
        public static (bool ok, object? value) Eval(string expr, Dictionary<string, object?> vars)
        {
            try
            {
                var p = new Parser(expr, vars);
                var v = p.ParseOr();
                p.SkipSpaces();
                if (!p.Eof) return (false, null);
                return (true, v);
            }
            catch { return (false, null); }
        }

        public static bool IsTrue(object? v) => v switch
        {
            null => false,
            bool b => b,
            double d => Math.Abs(d) > 1e-12,
            string s => !string.IsNullOrEmpty(s) && s != "0" && s.ToLowerInvariant() != "false",
            _ => true
        };

        private class Parser
        {
            private readonly string _s;
            private readonly Dictionary<string, object?> _v;
            private int _i;
            public bool Eof => _i >= _s.Length;

            public Parser(string s, Dictionary<string, object?> v) { _s = s; _v = v; }

            public void SkipSpaces() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            private bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

            public object? ParseOr()
            {
                var left = ParseAnd();
                while (true)
                {
                    SkipSpaces();
                    if (MatchWord("or") || MatchOp("||")) left = IsTrue(left) || IsTrue(ParseAnd());
                    else break;
                }
                return left;
            }

            private object? ParseAnd()
            {
                var left = ParseNot();
                while (true)
                {
                    SkipSpaces();
                    if (MatchWord("and") || MatchOp("&&")) left = IsTrue(left) && IsTrue(ParseNot());
                    else break;
                }
                return left;
            }

            private object? ParseNot()
            {
                SkipSpaces();
                if (MatchWord("not") || MatchOp("!")) return !IsTrue(ParseNot());
                return ParseCmp();
            }

            private object? ParseCmp()
            {
                var left = ParseAdd();
                SkipSpaces();
                string? op = null;
                if (MatchOp(">=")) op = ">=";
                else if (MatchOp("<=")) op = "<=";
                else if (MatchOp("==")) op = "==";
                else if (MatchOp("!=")) op = "!=";
                else if (MatchOp(">")) op = ">";
                else if (MatchOp("<")) op = "<";
                if (op == null) return left;
                var right = ParseAdd();
                return Compare(left, right, op);
            }

            private object? ParseAdd()
            {
                var left = ParseMul();
                while (true)
                {
                    SkipSpaces();
                    if (MatchOp("+")) left = Num(left) + Num(ParseMul());
                    else if (MatchOp("-")) left = Num(left) - Num(ParseMul());
                    else break;
                }
                return left;
            }

            private object? ParseMul()
            {
                var left = ParseUnary();
                while (true)
                {
                    SkipSpaces();
                    if (MatchOp("*")) left = Num(left) * Num(ParseUnary());
                    else if (MatchOp("/")) left = Num(left) / Num(ParseUnary());
                    else break;
                }
                return left;
            }

            private object? ParseUnary()
            {
                SkipSpaces();
                if (MatchOp("-")) return -Num(ParseUnary());
                if (MatchOp("+")) return Num(ParseUnary());
                return ParsePrimary();
            }

            private object? ParsePrimary()
            {
                SkipSpaces();
                if (_i >= _s.Length) throw new FormatException();
                char c = _s[_i];
                if (c == '(') { _i++; var v = ParseOr(); SkipSpaces(); Expect(')'); return v; }
                if (c == '\'' || c == '"')
                {
                    char q = c; _i++;
                    var sb = new StringBuilder();
                    while (_i < _s.Length && _s[_i] != q) { sb.Append(_s[_i]); _i++; }
                    _i++;
                    return sb.ToString();
                }
                if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    return double.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
                }
                // 变量（中文/字母开头）
                int st = _i;
                while (_i < _s.Length && IsIdent(_s[_i])) _i++;
                string name = _s.Substring(st, _i - st);
                if (name.Length == 0) throw new FormatException();
                if (name.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                return _v.TryGetValue(name, out var val) ? val : null;
            }

            private bool MatchOp(string op)
            {
                SkipSpaces();
                if (_i + op.Length <= _s.Length && _s.Substring(_i, op.Length) == op)
                { _i += op.Length; return true; }
                return false;
            }

            private bool MatchWord(string word)
            {
                SkipSpaces();
                if (_i + word.Length <= _s.Length
                    && _s.Substring(_i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)
                    && (_i + word.Length == _s.Length || !IsIdent(_s[_i + word.Length])))
                { _i += word.Length; return true; }
                return false;
            }

            private void Expect(char c) { SkipSpaces(); if (_i < _s.Length && _s[_i] == c) { _i++; return; } throw new FormatException(); }

            private static double Num(object? v) => v switch
            {
                null => 0,
                double d => d,
                bool b => b ? 1 : 0,
                string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0,
                _ => Convert.ToDouble(v)
            };

            private static bool Compare(object? l, object? r, string op)
            {
                // 两边都能当数字则按数值比，否则按字符串比
                double dl = 0, dr = 0;
                bool numeric = TryNum(l, out dl) && TryNum(r, out dr);
                if (numeric) return Cmp(dl, dr, op);
                string sl = l?.ToString() ?? "", sr = r?.ToString() ?? "";
                int cmpOrd = string.CompareOrdinal(sl, sr);
                return op switch
                {
                    "==" => sl == sr,
                    "!=" => sl != sr,
                    ">" => cmpOrd > 0,
                    "<" => cmpOrd < 0,
                    ">=" => cmpOrd >= 0,
                    "<=" => cmpOrd <= 0,
                    _ => false
                };
            }

            private static bool TryNum(object? v, out double d)
            {
                switch (v)
                {
                    case double dd: d = dd; return true;
                    case bool b: d = b ? 1 : 0; return true;
                    case string s: return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
                    default: d = 0; return false;
                }
            }

            private static bool Cmp(double l, double r, string op) => op switch
            {
                ">" => l > r, "<" => l < r, ">=" => l >= r, "<=" => l <= r,
                "==" => Math.Abs(l - r) < 1e-9, "!=" => Math.Abs(l - r) >= 1e-9,
                _ => false
            };
        }
    }

    // ===================== 我的模块（子流程打包/导入） =====================
    /// <summary>模块就是一段没有开始/结束的流程图：入口节点 + 内部节点 + 内部连线。</summary>
    public class SubflowMod
    {
        public string Name = "我的模块";
        public string Description = "";
        public string EntryId = "";
        public List<FlowNode> Nodes = new();
        public List<FlowEdge> Edges = new();

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = true,
            IncludeFields = true
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpt);

        public static SubflowMod FromJson(string json)
        {
            var m = JsonSerializer.Deserialize<SubflowMod>(json, JsonOpt) ?? new SubflowMod();
            m.Nodes ??= new List<FlowNode>();
            m.Edges ??= new List<FlowEdge>();
            foreach (var n in m.Nodes) n.Params ??= new Dictionary<string, string>();
            return m;
        }

        public FlowNode? Entry()
        {
            var e = Nodes.Find(n => n.Id == EntryId);
            if (e != null) return e;
            EntryId = SubflowLibrary.InferEntryId(Nodes, Edges, null) ?? "";
            return Nodes.Find(n => n.Id == EntryId);
        }

        /// <summary>执行器使用：模块作为一层调用图（节点/边直接引用，执行期只读）</summary>
        public FlowGraph ToFlowGraph() => new() { Name = Name, Nodes = Nodes, Edges = Edges };
    }

    /// <summary>工具箱列表用的轻量引用（不解析全部节点）</summary>
    public class SubflowRef
    {
        public string File = "";
        public string Name = "";
        public string Description = "";
        public int NodeCount;
        public bool Valid = true;
        public string Error = "";
    }

    /// <summary>nodemods 目录管理：扫描/加载/保存/导入 .subflow.json</summary>
    public static class SubflowLibrary
    {
        public static string Dir =>
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nodemods");

        public static string ResolvePath(string file) => System.IO.Path.Combine(Dir, file);

        public static List<SubflowRef> List()
        {
            var result = new List<SubflowRef>();
            try
            {
                if (!Directory.Exists(Dir)) return result;
                foreach (var f in Directory.GetFiles(Dir, "*.subflow.json"))
                {
                    var r = new SubflowRef { File = System.IO.Path.GetFileName(f) };
                    try
                    {
                        var m = FromFile(f);
                        r.Name = string.IsNullOrWhiteSpace(m.Name) ? r.File : m.Name;
                        r.Description = m.Description ?? "";
                        r.NodeCount = m.Nodes.Count;
                        r.Valid = m.Nodes.Count > 0 && m.Entry() != null;
                        if (!r.Valid) r.Error = "模块为空或找不到入口节点";
                    }
                    catch (Exception ex) { r.Valid = false; r.Error = ex.Message; }
                    result.Add(r);
                }
            }
            catch { }
            return result.OrderBy(x => x.Name).ToList();
        }

        public static SubflowMod? Load(string file)
        {
            try { return FromFile(ResolvePath(file)); }
            catch { return null; }
        }

        public static SubflowMod FromFile(string path)
            => SubflowMod.FromJson(File.ReadAllText(path));

        /// <summary>保存为新模块（按模块名生成唯一文件名），返回文件名</summary>
        public static string SaveNew(SubflowMod mod)
        {
            Directory.CreateDirectory(Dir);
            string baseName = SafeName(mod.Name);
            string file = baseName + ".subflow.json";
            int i = 2;
            while (File.Exists(ResolvePath(file))) file = $"{baseName}_{i++}.subflow.json";
            File.WriteAllText(ResolvePath(file), mod.ToJson());
            return file;
        }

        /// <summary>覆盖保存到指定模块文件（模块编辑器用；模块改名也保持同一文件）</summary>
        public static void SaveAs(SubflowMod mod, string file)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ResolvePath(file), mod.ToJson());
        }

        /// <summary>导入外部 .subflow.json 到 nodemods（重名自动加序号），返回入库后的文件名</summary>
        public static string ImportFile(string srcPath)
        {
            var m = FromFile(srcPath);
            if (string.IsNullOrWhiteSpace(m.Name)) m.Name = System.IO.Path.GetFileNameWithoutExtension(srcPath);
            return SaveNew(m);
        }

        public static void Delete(string file)
        {
            try { File.Delete(ResolvePath(file)); } catch { }
        }

        public static string SafeName(string name)
        {
            var s = (name ?? "").Trim();
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Replace('.', '_');
            return string.IsNullOrWhiteSpace(s) ? "我的模块" : s;
        }

        /// <summary>
        /// 推断模块入口：优先「外部入边进入的节点」（打包时提供 externalIn），
        /// 其次取内部入度为 0 的节点；仍有多个则取画布最靠上(Y最小)的。
        /// </summary>
        public static string? InferEntryId(List<FlowNode> nodes, List<FlowEdge> internalEdges, HashSet<string>? externalIn)
        {
            if (nodes.Count == 0) return null;
            if (externalIn != null)
            {
                var hit = nodes.Where(n => externalIn.Contains(n.Id))
                               .OrderBy(n => n.Y).ThenBy(n => n.X).FirstOrDefault();
                if (hit != null) return hit.Id;
            }
            var incoming = new HashSet<string>(internalEdges.Select(e => e.To));
            var roots = nodes.Where(n => !incoming.Contains(n.Id))
                             .OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
            return (roots.Count > 0 ? roots[0] : nodes.OrderBy(n => n.Y).First()).Id;
        }
    }
}

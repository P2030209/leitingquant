using System;
using System.Drawing;

namespace WindowSpy
{
    public enum ActionType { Click, Ocr, Condition, Save, Expression, LoopStart, LoopEnd, BringFront, KeyPress, IfStart, ElseIf, Else, EndIf, BreakLoop, ContinueLoop, Goto, Label, BreakBlock, Network, Comment, Quant, MarketPull, AiAsk, BanCheck }
    public enum TargetType { A, B }

    public class ScriptStep
    {
        public ActionType Type { get; set; }
        public TargetType Target { get; set; }
        public Rectangle Rect { get; set; }
        public Point Point { get; set; }
        public int DelayMs { get; set; }
        public int RandomDelay { get; set; } = 0;
        public int DwellMs { get; set; }
        public int RandomDwell { get; set; } = 0;
        public int RandomX { get; set; } = 0;
        public int RandomY { get; set; } = 0;
        public string Pattern { get; set; } = "";
        public string Key { get; set; } = "";
        public int Count { get; set; }
        public bool? LastResult { get; set; } = null;
        public bool JumpOnTrue { get; set; } = false;
        public bool OcrNumbersOnly { get; set; } = false;
        public bool ReuseOcrOnRoiUnchanged { get; set; } = false;
        public string NetworkAdapterName { get; set; } = "";
        public bool NetworkEnable { get; set; } = false;
        public bool NetworkSync { get; set; } = false;
        public string QuantFunc { get; set; } = "MA";
        public int QuantParam { get; set; } = 20;
        public string ResultKey { get; set; } = "";
        public string MarketUrl { get; set; } = "";
        public string PriceVar { get; set; } = "";
        public string ChangeVar { get; set; } = "";
        public bool SeedHistory { get; set; } = true;
        public int HistoryLimit { get; set; } = 60;
        public bool AutoPick { get; set; } = false;
        public string PickStrategy { get; set; } = "RISE";
        public string BulletVar { get; set; } = "";
        // AI 决策（DeepSeek）：Prompt 支持 {变量名} 替换，回答文本存 ResultKey
        public string AiPrompt { get; set; } = "";
        public string AiModel { get; set; } = "deepseek-chat";
        // 风控：OCR 全窗扫描封禁关键词（逗号分隔），命中即自动停止全部执行
        public string BanKeywords { get; set; } = "";
    }
}

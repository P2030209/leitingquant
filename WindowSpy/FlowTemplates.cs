using System.Collections.Generic;

namespace WindowSpy
{
    /// <summary>
    /// 内置可执行流程模板（纯建图逻辑，不依赖 UI）。
    /// 每套模板建图后统一 FlowLayout.Arrange 纵向排版。
    /// 坐标类参数默认留空，由用户在游戏里点选；变量引用已按节点输出预置。
    /// </summary>
    public static class FlowTemplates
    {
        public sealed class Template
        {
            public required string Key { get; init; }
            public required string Name { get; init; }
            public required string Desc { get; init; }
            public required FlowGraph Graph { get; init; }
            public required List<string> Tips { get; init; }
        }

        /// <summary>全部内置模板（工具栏下拉/按钮用）</summary>
        public static List<Template> All() => new()
        {
            Standard(), FullTrade(), QuickSell(), DipBuy(), Watch(), MailTest(), Recycle(),
            LoopForever(), AnnounceOnly(), RelistLoop(), MailRecycle(),
            LuaCustomPick(), PythonStatsReport(), LuaConditionBranch(),
            AiSmartBuy(), AiHoldManager(), AiQuantFull(), MultiStrategyTest(), LuaPositionSizing(), MultiItemPortfolio(),
            AiWatch()
        };

        public static Template ByKey(string key)
        {
            foreach (var t in All()) if (t.Key == key) return t;
            return Standard();
        }

        // —— 构建小工具 ——
        private sealed class B
        {
            public readonly FlowGraph G = new();
            public B(string name) { G.Name = name; }
            public FlowNode N(string type)
            {
                var n = G.AddNode(type, 0, 0);
                return n;
            }
            public void L(FlowNode a, string port, FlowNode b)
                => G.Edges.Add(new FlowEdge { Id = G.NewId("e"), From = a.Id, Port = port, To = b.Id });
            public Template Finish(string key, string name, string desc, List<string> tips)
            {
                FlowLayout.Arrange(G);
                return new Template { Key = key, Name = name, Desc = desc, Graph = G, Tips = tips };
            }
        }

        // ================= ① 新手测试：领邮件 + 读余额（零买卖，最安全） =================
        public static Template MailTest()
        {
            var b = new B("新手测试-领币读余额");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "第一步只验证两件事：能领到邮件哈弗币、余额识别正确(4,002K=4002000)";
            var ban = b.N("ban_check");
            var mail = b.N("claim_mail");
            var entered = b.N("enter_trade");
            var tab = b.N("trade_tab"); tab.Params["tab"] = "购买"; tab.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var log = b.N("log_msg");
            log.Params["text"] = "当前哈弗币余额：{余额}"; log.Params["level"] = "普通";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", mail);
            b.L(ban, "hit", beep);
            b.L(mail, "", entered); b.L(entered, "", tab); b.L(tab, "", wallet);
            b.L(wallet, "", log); b.L(log, "", end);
            b.L(beep, "", end);

            return b.Finish("mailtest", "①新手测试·领币读余额",
                "只领邮件、进交易行读余额并播报，不买不卖，首次配置坐标时用它验证",
                new List<string>
                {
                    "必配坐标：邮箱入口/一键领取/关闭(可空)、交易行入口、购买页签、余额框选区域",
                    "成功标准：日志打印「领取成功」且余额数字正确（K=千 M=百万 万=×10000）",
                    "封禁弹窗词命中会直接响铃结束，不会做任何操作"
                });
        }

        // ================= ② 行情盯盘播报（只看行情不动鼠标，验证选品策略） =================
        public static Template Watch()
        {
            var b = new B("行情盯盘播报");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "纯网络行情轮询：每60秒选品一次并播报/响铃，不操作游戏，可最小化挂机";
            var loop = b.N("loop_n"); loop.Params["count"] = "60";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "SMART";
            var has = b.N("if"); has.Params["expr"] = "选品预期利润 >= 3";
            var found = b.N("log_msg");
            found.Params["text"] = "机会：{选品品名} 现价{选品现价} 份数{选品份数} 预期利润{选品预期利润}%";
            found.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var miss = b.N("log_msg");
            miss.Params["text"] = "本轮无达标品种（税后利润<3%），60秒后再扫";
            miss.Params["level"] = "等待";
            var wait = b.N("delay"); wait.Params["sec"] = "60";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", loop);
            b.L(loop, "body", fetch);
            b.L(fetch, "", pick);
            b.L(pick, "ok", has);
            b.L(pick, "miss", miss);
            b.L(has, "true", found);
            b.L(has, "false", miss);
            b.L(found, "", beep);
            b.L(beep, "", wait);
            b.L(miss, "", wait);
            // wait 无出边：隐式回流 loop 头；跑满 60 轮 → done
            b.L(loop, "done", end);

            return b.Finish("watch", "②行情盯盘·机会播报",
                "每60秒拉行情+SMART选品，达标响铃并打印品名/价格/份数/利润，不操作游戏",
                new List<string>
                {
                    "无需任何游戏坐标，能联网即可运行",
                    "用途：验证行情接口与选品策略，听到铃声再手动进场",
                    "想改频率改「延时等待」秒数；改门槛改如果节点的 3"
                });
        }

        // ================= ③ 抄底建仓（单次：选超跌品→买入一份，买完即停） =================
        public static Template DipBuy()
        {
            var b = new B("抄底建仓(单次)");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "单次抄底：DIP策略选超跌反弹品种，达标买1份，买完结束。请先在交易行购买页";
            var ban = b.N("ban_check");
            var enter = b.N("enter_trade");
            var tab = b.N("trade_tab"); tab.Params["tab"] = "购买"; tab.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "DIP"; pick.Params["minprofit"] = "4";
            var ok = b.N("if"); ok.Params["expr"] = "选品预期利润 >= 4";
            var search = b.N("select_bullet");
            var lots = b.N("adjust_lots"); lots.Params["target"] = "1";
            var buy = b.N("buy");
            var blog = b.N("log_msg");
            blog.Params["text"] = "已抄底买入 {选品品名} ×1，成交价{选品现价}，后续请用秒卖模板或盯盘流程管理";
            blog.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var nolog = b.N("log_msg");
            nolog.Params["text"] = "当前无超跌达标品种，未买入"; nolog.Params["level"] = "等待";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", enter);
            b.L(ban, "hit", end);
            b.L(enter, "", tab); b.L(tab, "", wallet); b.L(wallet, "", fetch);
            b.L(fetch, "", pick);
            b.L(pick, "ok", ok);
            b.L(pick, "miss", nolog);
            b.L(ok, "true", search);
            b.L(ok, "false", nolog);
            b.L(search, "", lots); b.L(lots, "", buy); b.L(buy, "", blog);
            b.L(blog, "", beep); b.L(beep, "", end);
            b.L(nolog, "", end);

            return b.Finish("dipbuy", "③抄底建仓·单次买入",
                "DIP超跌策略选品，利润≥4% 搜索并买入 1 份后结束，适合手动择时跑一次",
                new List<string>
                {
                    "必配坐标：交易行入口、购买页签、余额框、搜索框、结果第一项、数量−/+、买入按钮",
                    "份数固定1份（可在调整份数节点改）；只买一轮，不会连续出手",
                    "买入后接「④秒卖上架」或「💎完整交易流程」管理持仓"
                });
        }

        // ================= ④ 秒卖上架（手动一键：交易行定价上架当前选品） =================
        public static Template QuickSell()
        {
            var b = new B("秒卖上架(手动)");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "手动一键卖出：确保当前在交易行；默认删位法（参考价删1位点第四档）追快钱，可改直接输入价";
            var ban = b.N("ban_check");
            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var ammo = b.N("goto_ammo"); ammo.Params["pos"] = "";
            var item = b.N("sell_pick_item"); item.Params["box"] = "";
            var chan = b.N("sell_channel");
            chan.Params["channel"] = "交易行"; chan.Params["tradePos"] = ""; chan.Params["merchantPos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "删位法+价格柱"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = ""; price.Params["deldigits"] = "1";
            var qty = b.N("set_sell_qty");
            var list = b.N("list_confirm"); list.Params["pos"] = ""; list.Params["confirm"] = "";
            var log = b.N("log_msg");
            log.Params["text"] = "已上架 {选品品名} ×{选品份数}，定价{持仓卖出价}，等待成交";
            log.Params["level"] = "等待";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", sellTab);
            b.L(ban, "hit", end);
            b.L(sellTab, "", ammo); b.L(ammo, "", item); b.L(item, "", chan);
            b.L(chan, "", price); b.L(price, "", qty); b.L(qty, "", list);
            b.L(list, "", log); b.L(log, "", beep); b.L(beep, "", end);

            return b.Finish("quicksell", "④秒卖上架·一键卖出",
                "切出售页→弹药分类→选子弹→弹窗选交易行→删位法定价→数量→确认上架，适合有持仓时手动跑",
                new List<string>
                {
                    "必配坐标：出售页签、弹药分类、右侧仓库子弹位、弹窗「交易行」按钮、价格输入框、价格柱、数量−/+、上架按钮、二次确认(可空)",
                    "定价默认删位法秒出；求稳把节点改成「直接输入价」并填 {持仓卖出价}",
                    "出售物品名默认取 {选品品名}，单独用时手动改成具体子弹名"
                });
        }

        // ================= ⑤ 军需处批量清仓 =================
        public static Template Recycle()
        {
            var b = new B("军需处清仓");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "白绿品垃圾走军需处；蓝品以上挂交易行。先手动停到大厅/仓库页面";
            var openWh = b.N("open_warehouse"); openWh.Params["pos"] = "";
            var recycle = b.N("recycle_batch");
            recycle.Params["cart"] = ""; recycle.Params["selectall"] = "";
            recycle.Params["recycle"] = ""; recycle.Params["confirm"] = "";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var log = b.N("log_msg");
            log.Params["text"] = "军需处清仓完成"; log.Params["level"] = "等待";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", openWh);
            b.L(openWh, "", recycle); b.L(recycle, "", beep); b.L(beep, "", log); b.L(log, "", end);

            return b.Finish("recycle", "⑤军需处·批量清仓",
                "打开仓库→推车批量选取→(全选)→回收→确认，白绿品杂物快速变现",
                new List<string>
                {
                    "必配坐标：仓库入口、仓库底部推车图标、回收按钮；全选/确认没有就留空",
                    "蓝品以上子弹不要走这里，回收价远低于交易行"
                });
        }

        // ================= ⑥ 标准倒卖（选品→买→盯盘→卖 单循环教学版） =================
        public static Template Standard()
        {
            var b = new B("标准倒卖流程");
            var start = b.N("start");
            var loop = b.N("loop_n"); loop.Params["count"] = "999";
            var ban = b.N("ban_check");
            var mail = b.N("claim_mail");
            var enter = b.N("enter_trade");
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick");
            var miss = b.N("delay"); miss.Params["sec"] = "10";
            var searchB = b.N("select_bullet");
            var lotsB = b.N("adjust_lots");
            var buy = b.N("buy");
            var holdLoop = b.N("loop_while"); holdLoop.Params["expr"] = "1 == 1";
            var ban2 = b.N("ban_check");
            var hold = b.N("hold_quote");
            var d20 = b.N("delay"); d20.Params["sec"] = "20";
            var ifWin = b.N("if"); ifWin.Params["expr"] = "持仓浮盈 >= 8";
            var ifLoss = b.N("if"); ifLoss.Params["expr"] = "持仓浮盈 <= -5";
            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var ammo = b.N("goto_ammo"); ammo.Params["pos"] = "";
            var item = b.N("sell_pick_item"); item.Params["box"] = "";
            var chan = b.N("sell_channel"); chan.Params["channel"] = "交易行"; chan.Params["tradePos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "直接输入价"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = "";
            var qty = b.N("set_sell_qty");
            var list = b.N("list_confirm"); list.Params["pos"] = ""; list.Params["confirm"] = "";
            var brk = b.N("break");
            var end = b.N("end");

            b.L(start, "", loop);
            b.L(loop, "body", ban);
            b.L(ban, "safe", mail);
            b.L(ban, "hit", end);
            b.L(mail, "", enter); b.L(enter, "", wallet); b.L(wallet, "", fetch);
            b.L(fetch, "", pick);
            b.L(pick, "ok", searchB);
            b.L(pick, "miss", miss);
            b.L(searchB, "", lotsB); b.L(lotsB, "", buy); b.L(buy, "", holdLoop);
            b.L(holdLoop, "body", ban2);
            b.L(ban2, "safe", hold);
            b.L(ban2, "hit", end);
            b.L(hold, "", d20); b.L(d20, "", ifWin);
            b.L(ifWin, "false", ifLoss);
            b.L(ifWin, "true", sellTab);
            b.L(ifLoss, "true", sellTab);
            b.L(sellTab, "", ammo); b.L(ammo, "", item); b.L(item, "", chan);
            b.L(chan, "", price); b.L(price, "", qty); b.L(qty, "", list);
            b.L(list, "", brk);
            b.L(holdLoop, "done", loop);
            b.L(loop, "done", end);

            return b.Finish("standard", "⑥标准倒卖·教学版",
                "999轮循环：领币→选品→买入→持仓盯盘(止盈8%/止损5%)→交易行上架卖出",
                new List<string>
                {
                    "新手建议先跑①和②确认坐标/行情，再用这套",
                    "必配：邮箱3项、交易行入口、余额框、搜索框/结果/±/买入、出售页签/弹药/子弹位/弹窗交易行/价格框/±/上架",
                    "止盈止损阈值在两个「如果」节点修改"
                });
        }

        // ================= ⑦ 完整交易流程（全闭环 + 双层封禁急停 + 提醒） =================
        public static Template FullTrade()
        {
            var b = new B("完整交易流程");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var guide = b.N("note");
            guide.Params["text"] = "全闭环999轮：领币→选品→买入→盯盘→止盈/止损→上架。首次请先空跑坐标，F12急停";
            var ban1 = b.N("ban_check");

            var loop = b.N("loop_n"); loop.Params["count"] = "999";
            var mail = b.N("claim_mail");
            var enter = b.N("enter_trade");
            var tabBuy = b.N("trade_tab"); tabBuy.Params["tab"] = "购买"; tabBuy.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick");
            var scout = b.N("if"); scout.Params["expr"] = "选品预期利润 >= 3";
            var waitMiss = b.N("delay"); waitMiss.Params["sec"] = "15";

            var searchB = b.N("select_bullet");
            var lotsB = b.N("adjust_lots");
            var buy = b.N("buy");
            var settleBuy = b.N("delay"); settleBuy.Params["sec"] = "2";

            var holdLoop = b.N("loop_while"); holdLoop.Params["expr"] = "1 == 1";
            var ban2 = b.N("ban_check");
            var hold = b.N("hold_quote");
            var logHold = b.N("log_msg");
            logHold.Params["text"] = "{选品品名} 现价{持仓现价} 浮盈{持仓浮盈}% RSI{持仓RSI}";
            logHold.Params["level"] = "普通";
            var tick = b.N("delay"); tick.Params["sec"] = "20";
            var ifWin = b.N("if"); ifWin.Params["expr"] = "持仓浮盈 >= 8";
            var ifLoss = b.N("if"); ifLoss.Params["expr"] = "持仓浮盈 <= -5";

            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var sellAmmo = b.N("goto_ammo"); sellAmmo.Params["pos"] = "";
            var sellItem = b.N("sell_pick_item"); sellItem.Params["box"] = "";
            var sellChan = b.N("sell_channel");
            sellChan.Params["channel"] = "交易行"; sellChan.Params["tradePos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "直接输入价"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = "";
            var sqty = b.N("set_sell_qty");
            var listOk = b.N("list_confirm"); listOk.Params["pos"] = ""; listOk.Params["confirm"] = "";
            var listed = b.N("log_msg");
            listed.Params["text"] = "已按{持仓卖出价}上架{选品品名}×{选品份数}份，等待成交，进入下一轮";
            listed.Params["level"] = "等待";
            var listBeep = b.N("beep"); listBeep.Params["times"] = "1";
            var brk = b.N("break");

            var errBeep = b.N("beep"); errBeep.Params["times"] = "3";
            var errStop = b.N("stop_here");
            errStop.Params["text"] = "检测到封禁/异常弹窗，已紧急停止，请立即人工检查账号！";

            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", guide); b.L(guide, "", ban1);
            b.L(ban1, "safe", loop);
            b.L(ban1, "hit", errBeep);

            b.L(loop, "body", mail);
            b.L(mail, "", enter);
            b.L(enter, "", tabBuy);
            b.L(tabBuy, "", wallet);
            b.L(wallet, "", fetch);
            b.L(fetch, "", pick);
            b.L(pick, "ok", scout);
            b.L(pick, "miss", waitMiss);
            b.L(scout, "true", searchB);
            b.L(scout, "false", waitMiss);
            b.L(searchB, "", lotsB);
            b.L(lotsB, "", buy);
            b.L(buy, "", settleBuy);
            b.L(settleBuy, "", holdLoop);

            b.L(holdLoop, "body", ban2);
            b.L(ban2, "safe", hold);
            b.L(ban2, "hit", errBeep);
            b.L(hold, "", logHold);
            b.L(logHold, "", tick);
            b.L(tick, "", ifWin);
            b.L(ifWin, "false", ifLoss);
            b.L(ifWin, "true", sellTab);
            b.L(ifLoss, "true", sellTab);

            b.L(sellTab, "", sellAmmo);
            b.L(sellAmmo, "", sellItem);
            b.L(sellItem, "", sellChan);
            b.L(sellChan, "", price);
            b.L(price, "", sqty);
            b.L(sqty, "", listOk);
            b.L(listOk, "", listed);
            b.L(listed, "", listBeep);
            b.L(listBeep, "", brk);

            b.L(holdLoop, "done", loop);
            b.L(loop, "done", end);
            b.L(errBeep, "", errStop);

            return b.Finish("full", "⑦完整交易·全自动闭环",
                "999轮全自动：双层封禁检测急停、领币、选品、买入、盯盘、止盈止损、交易行上架、成交提醒",
                new List<string>
                {
                    "完整坐标清单：邮箱入口/领取/关闭、交易行入口、购买页签、余额框、搜索框/结果/数量−+/买入、出售页签/弹药分类/右侧子弹/弹窗交易行/价格框/数量−+/上架/二次确认",
                    "安全：两层封禁检测命中→响铃3声+主动停止；任何节点失败→红色闪烁并停在该步",
                    "阈值：选品门槛3%、止盈+8%、止损-5%、盯盘20秒，均在对应节点修改"
                });
        }

        // ================= ⑧ 无限托管闭环（count=0，仅 F12/暂停 退出） =================
        public static Template LoopForever()
        {
            var b = new B("无限托管闭环");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "无限循环托管（循环次数=0，永不自动结束）：暂停可继续，只有F12急停才退出。请先跑通⑥再用这套长时间挂机";
            var ban0 = b.N("ban_check");
            var loop = b.N("loop_n"); loop.Params["count"] = "0";
            var mail = b.N("claim_mail");
            var enter = b.N("enter_trade");
            var tab = b.N("trade_tab"); tab.Params["tab"] = "购买"; tab.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick");
            var scout = b.N("if"); scout.Params["expr"] = "选品预期利润 >= 3";
            var miss = b.N("delay"); miss.Params["sec"] = "15";
            var searchB = b.N("select_bullet");
            var lotsB = b.N("adjust_lots");
            var buy = b.N("buy");

            var holdLoop = b.N("loop_while"); holdLoop.Params["expr"] = "1 == 1";
            var ban2 = b.N("ban_check");
            var hold = b.N("hold_quote");
            var tick = b.N("delay"); tick.Params["sec"] = "20";
            var ifWin = b.N("if"); ifWin.Params["expr"] = "持仓浮盈 >= 8";
            var ifLoss = b.N("if"); ifLoss.Params["expr"] = "持仓浮盈 <= -5";
            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var ammo = b.N("goto_ammo"); ammo.Params["pos"] = "";
            var item = b.N("sell_pick_item"); item.Params["box"] = "";
            var chan = b.N("sell_channel"); chan.Params["channel"] = "交易行"; chan.Params["tradePos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "直接输入价"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = "";
            var qty = b.N("set_sell_qty");
            var list = b.N("list_confirm"); list.Params["pos"] = ""; list.Params["confirm"] = "";
            var listed = b.N("log_msg");
            listed.Params["text"] = "已上架{选品品名}×{选品份数}，等成交后自动进入下一轮";
            listed.Params["level"] = "等待";
            var brk = b.N("break");
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban0);
            b.L(ban0, "safe", loop);
            b.L(ban0, "hit", end);

            b.L(loop, "body", mail);
            b.L(mail, "", enter); b.L(enter, "", tab); b.L(tab, "", wallet);
            b.L(wallet, "", fetch); b.L(fetch, "", pick);
            b.L(pick, "ok", scout);
            b.L(pick, "miss", miss);
            b.L(scout, "true", searchB);
            b.L(scout, "false", miss);
            b.L(searchB, "", lotsB); b.L(lotsB, "", buy); b.L(buy, "", holdLoop);

            b.L(holdLoop, "body", ban2);
            b.L(ban2, "safe", hold);
            b.L(ban2, "hit", end);
            b.L(hold, "", tick); b.L(tick, "", ifWin);
            b.L(ifWin, "false", ifLoss);
            b.L(ifWin, "true", sellTab);
            b.L(ifLoss, "true", sellTab);
            b.L(sellTab, "", ammo); b.L(ammo, "", item); b.L(item, "", chan);
            b.L(chan, "", price); b.L(price, "", qty); b.L(qty, "", list);
            b.L(list, "", listed); b.L(listed, "", brk);

            b.L(holdLoop, "done", loop);
            b.L(loop, "done", end);

            return b.Finish("loop_forever", "⑧无限托管·闭环挂机",
                "外层循环次数=0：领币→选品→买入→盯盘→止盈止损→上架，永不停轮，仅暂停/F12可退出",
                new List<string>
                {
                    "必配坐标：邮箱3项、交易行入口、购买/出售页签、余额框、搜索框/结果/数量−+/买入、弹药分类/右侧子弹/弹窗交易行/价格框/上架/二次确认",
                    "循环节点 count=0 表示无限（日志每轮打印轮次号）；想限时改成具体次数",
                    "建议先用⑥标准倒卖白天跑通两轮，确认无误再用这套夜间挂机；F9呼出窗口，F12立即急停"
                });
        }

        // ================= ⑨ 只播报不下单（领币+无限行情播报，零买入零上架） =================
        public static Template AnnounceOnly()
        {
            var b = new B("只播报不下单");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "只播报：自动领邮件哈弗币，之后无限轮询行情，达标机会只响铃+打印，绝不点买入/上架";
            var ban = b.N("ban_check");
            var mail = b.N("claim_mail");
            var loop = b.N("loop_n"); loop.Params["count"] = "0";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "SMART";
            var has = b.N("if"); has.Params["expr"] = "选品预期利润 >= 3";
            var found = b.N("log_msg");
            found.Params["text"] = "🔔机会：{选品品名} 现价{选品现价} 可买{选品份数}份 预期利润{选品预期利润}%（需手动进场）";
            found.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var miss = b.N("log_msg");
            miss.Params["text"] = "本轮无达标品种（税后利润<3%），60秒后再扫";
            miss.Params["level"] = "等待";
            var wait = b.N("delay"); wait.Params["sec"] = "60";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", mail);
            b.L(ban, "hit", end);
            b.L(mail, "", loop);
            b.L(loop, "body", fetch);
            b.L(fetch, "", pick);
            b.L(pick, "ok", has);
            b.L(pick, "miss", miss);
            b.L(has, "true", found);
            b.L(has, "false", miss);
            b.L(found, "", beep);
            b.L(beep, "", wait);
            b.L(miss, "", wait);
            // wait 无出边：隐式回流 loop 头；count=0 无限
            b.L(loop, "done", end);

            return b.Finish("announce_only", "⑨只播报·不下单",
                "领币后无限轮询行情（60秒/轮），SMART选品达标只响铃打印品名/价格/份数/利润，不做任何买卖操作",
                new List<string>
                {
                    "唯一必配：邮箱入口/一键领取/关闭（可空）；行情走网络接口，无需交易行坐标",
                    "改门槛：如果节点的 3；改频率：延时节点的 60 秒；换策略：选品节点 strategy",
                    "听到铃声再手动开游戏进场，适合上班/夜间只想蹲机会的场景；F12停止"
                });
        }

        // ================= ⑩ 秒卖补货循环（上架→等成交→买回1份，无限周转） =================
        public static Template RelistLoop()
        {
            var b = new B("秒卖补货循环");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "秒卖补货无限循环：读持仓→删位法秒上架→等30秒成交→选品买回1份→继续。启动前账户须持有1份子弹";
            var ban = b.N("ban_check");
            var loop = b.N("loop_n"); loop.Params["count"] = "0";

            var hold = b.N("hold_quote");
            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var ammo = b.N("goto_ammo"); ammo.Params["pos"] = "";
            var item = b.N("sell_pick_item"); item.Params["box"] = "";
            var chan = b.N("sell_channel"); chan.Params["channel"] = "交易行"; chan.Params["tradePos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "删位法+价格柱"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = ""; price.Params["deldigits"] = "1";
            var qty = b.N("set_sell_qty");
            var list = b.N("list_confirm"); list.Params["pos"] = ""; list.Params["confirm"] = "";
            var listed = b.N("log_msg");
            listed.Params["text"] = "已秒上架{选品品名}×{选品份数}，等待30秒成交后补货";
            listed.Params["level"] = "等待";
            var waitDeal = b.N("delay"); waitDeal.Params["sec"] = "30";

            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick");
            var ok = b.N("if"); ok.Params["expr"] = "选品预期利润 >= 3";
            var searchB = b.N("select_bullet");
            var lotsB = b.N("adjust_lots"); lotsB.Params["target"] = "1";
            var buy = b.N("buy");
            var blog = b.N("log_msg");
            blog.Params["text"] = "已补回 {选品品名} ×1 @{选品现价}，下一轮继续秒卖";
            blog.Params["level"] = "动作";
            var settle = b.N("delay"); settle.Params["sec"] = "10";
            var nolog = b.N("log_msg");
            nolog.Params["text"] = "暂无达标补货品种，30秒后重扫再补"; nolog.Params["level"] = "等待";
            var retry = b.N("delay"); retry.Params["sec"] = "30";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", loop);
            b.L(ban, "hit", end);

            b.L(loop, "body", hold);
            b.L(hold, "", sellTab);
            b.L(sellTab, "", ammo); b.L(ammo, "", item); b.L(item, "", chan);
            b.L(chan, "", price); b.L(price, "", qty); b.L(qty, "", list);
            b.L(list, "", listed); b.L(listed, "", waitDeal);
            b.L(waitDeal, "", fetch); b.L(fetch, "", pick);
            b.L(pick, "ok", ok);
            b.L(pick, "miss", nolog);
            b.L(ok, "true", searchB);
            b.L(ok, "false", nolog);
            b.L(searchB, "", lotsB); b.L(lotsB, "", buy); b.L(buy, "", blog);
            b.L(blog, "", settle);
            b.L(nolog, "", retry);
            // settle/retry 无出边：隐式回流 loop 头，无限下一轮
            b.L(loop, "done", end);

            return b.Finish("relist_loop", "⑩秒卖补货·周转循环",
                "无限循环：读持仓→删位法秒上架→等成交→选品买回1份，适合低买高卖刷周转",
                new List<string>
                {
                    "启动前必须已持有至少1份子弹（首个「读持仓」节点读不到会出错暂停，属正常保护）",
                    "必配坐标：出售页签/弹药分类/右侧子弹/弹窗交易行/价格框/价格柱/数量−+/上架/二次确认 + 搜索框/结果/数量−+/买入",
                    "秒卖用删位法（删1位点第四档柱），等成交默认30秒、补货门槛3%、固定买1份，均可在节点上改"
                });
        }

        // ================= ⑪ 领币 + 军需处清仓一体（单次，零交易行操作） =================
        public static Template MailRecycle()
        {
            var b = new B("领币清仓一体");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "一条龙：领邮件哈弗币 → 打开仓库 → 军需处批量回收白绿品。蓝品以上子弹会被一起回收前请自行转移";
            var ban = b.N("ban_check");
            var mail = b.N("claim_mail");
            var settle = b.N("delay"); settle.Params["sec"] = "1";
            var openWh = b.N("open_warehouse"); openWh.Params["pos"] = "";
            var recycle = b.N("recycle_batch");
            recycle.Params["cart"] = ""; recycle.Params["selectall"] = "";
            recycle.Params["recycle"] = ""; recycle.Params["confirm"] = "";
            var log = b.N("log_msg");
            log.Params["text"] = "领币+军需处清仓全部完成"; log.Params["level"] = "等待";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", mail);
            b.L(ban, "hit", end);
            b.L(mail, "", settle); b.L(settle, "", openWh);
            b.L(openWh, "", recycle); b.L(recycle, "", log); b.L(log, "", beep); b.L(beep, "", end);

            return b.Finish("mail_recycle", "⑪领币清仓·一条龙",
                "领邮件哈弗币后直接开仓库→推车批量选取→军需处回收，单次执行，白绿品杂物+邮件一键清",
                new List<string>
                {
                    "必配坐标：邮箱入口/一键领取/关闭(可空)、仓库入口、推车图标、回收按钮、确认按钮（全选可空）",
                    "回收前自行把蓝品以上子弹/贵重物转移，军需处会把选中物品全部低价回收",
                    "单次流程不循环，跑完响铃2声自动结束"
                });
        }

        // ================= ⑫ Lua 自定义选品策略 =================
        public static Template LuaCustomPick()
        {
            var b = new B("Lua自定义选品策略");
            var start = b.N("start");
            var log1 = b.N("log_msg"); log1.Params["text"] = "Lua自定义选品：用脚本替代硬编码策略";
            var fetch = b.N("fetch_quotes");
            var lua = b.N("lua_script");
            lua.Params["code"] = @"-- 自定义选品逻辑：遍历行情列表，选综合评分最高的
-- vars['行情列表'] = [{name, price, profit, ...}, ...]
local m = vars['行情列表']
if not m or #m == 0 then
  log('warn', '行情列表为空')
  vars['选品品名'] = ''
  return
end

local best = nil
local bestScore = -9999
for k, v in ipairs(m) do
  -- 评分公式：利润 × 0.6 + 低价优势 × 0.4 - 手续费
  local profit = tonumber(v.profit or 0) or 0
  local price  = tonumber(v.price or 0) or 0
  local score = profit * 0.6 + (2000 - price) * 0.4
  if score > bestScore then
    bestScore = score
    best = v
  end
end

if best then
  vars['选品品名'] = best.name or ''
  vars['选品现价'] = tostring(best.price or 0)
  vars['选品预期利润'] = tostring(best.profit or 0)
  vars['选品份数'] = '1'
  log('info', 'Lua选品：' .. vars['选品品名'] .. ' 评分=' .. string.format('%.1f', bestScore))
else
  log('warn', 'Lua未选出合适品')
end";
            var ifNode = b.N("if"); ifNode.Params["expr"] = "选品品名 != \"\"";
            var log2 = b.N("log_msg"); log2.Params["text"] = "选出 {选品品名}，现价 {选品现价}";
            var beep = b.N("beep");
            var note = b.N("note"); note.Params["text"] = "🔧 在这里接后续的搜索/买入/上架节点";
            var end1 = b.N("end");
            var log3 = b.N("log_msg"); log3.Params["text"] = "Lua脚本未选出品，跳过交易";

            b.L(start, "", log1); b.L(log1, "", fetch); b.L(fetch, "", lua); b.L(lua, "", ifNode);
            b.L(ifNode, "true", log2); b.L(log2, "", beep); b.L(beep, "", note); b.L(note, "", end1);
            b.L(ifNode, "false", log3); b.L(log3, "", end1);

            return b.Finish("lua_custom_pick", "⑫ Lua自定义选品策略",
                "Lua脚本替代硬编码选品策略：遍历行情列表按自定义评分公式选出最佳品",
                new List<string>
                {
                    "必配坐标：fetch_quotes 的交易行入口/刷新/第一行等（按模板已有标定清单）",
                    "Lua脚本里 vars 是全局 table：行情列表/选品结果都存在里面",
                    "改评分公式就能变成你专属的策略，不用改 C# 源码"
                });
        }

        // ================= ⑬ Python 行情统计分析 =================
        public static Template PythonStatsReport()
        {
            var b = new B("Python行情统计分析");
            var start = b.N("start");
            var fetch = b.N("fetch_quotes");
            var py = b.N("python_script");
            py.Params["code"] = @"# Python统计：均价/标准差/最高/最低/数量
# vars 是流程变量字典
import json, statistics

inp = json.loads(os.environ.get('__INPUT__', '{}'))
vars = inp.get('vars', {})

prices_raw = vars.get('行情列表', [])
if isinstance(prices_raw, list) and len(prices_raw) > 0:
    prices = [float(p.get('price', 0)) for p in prices_raw]
    vars['均价'] = round(statistics.mean(prices), 2)
    vars['最高'] = max(prices)
    vars['最低'] = min(prices)
    vars['数量'] = len(prices)
    if len(prices) > 1:
        vars['标准差'] = round(statistics.stdev(prices), 2)
    else:
        vars['标准差'] = 0
    vars['价格区间'] = vars['最高'] - vars['最低']
    print(json.dumps({'vars': vars}))
else:
    print(json.dumps({'vars': {'数量': 0}}))";
            py.Params["python"] = "python";
            var log1 = b.N("log_msg"); log1.Params["text"] = "📊 行情统计：{数量}个品 | 均价 {均价} | 区间 {最低}-{最高} | σ={标准差}";
            var beep = b.N("beep"); beep.Params["times"] = "1";
            var end1 = b.N("end");

            b.L(start, "", fetch); b.L(fetch, "", py); b.L(py, "", log1); b.L(log1, "", beep); b.L(beep, "", end1);

            return b.Finish("python_stats_report", "⑬ Python行情统计分析",
                "Python 外部脚本：均价/标准差/最高最低/数量统计；需要本机装 python 并在 PATH",
                new List<string>
                {
                    "必配坐标：fetch_quotes 的交易行入口/刷新",
                    "需要 python3 在 PATH（节点参数可填完整路径如 C:\\Python312\\python.exe）",
                    "脚本内部 stdout 输出 JSON{vars:{...}} 即自动回写流程变量"
                });
        }

        // ================= ⑭ Lua 条件分支三路 =================
        public static Template LuaConditionBranch()
        {
            var b = new B("Lua条件分支三路");
            var start = b.N("start");
            var note = b.N("note"); note.Params["text"] = "演示：Lua脚本判断行情趋势 → 三选一分支";
            var fetch = b.N("fetch_quotes");
            var lua = b.N("lua_script");
            lua.Params["code"] = @"-- 用 Lua 判断行情趋势，结果写入 vars['分支']='up'/'down'/'flat'
local m = vars['行情列表']
if not m or #m < 3 then
  vars['分支'] = 'flat'
  return
end

-- 取前3个品的平均利润对比
local top = 0
for i = 1, math.min(3, #m) do
  top = top + (tonumber(m[i].profit or 0) or 0)
end
top = top / 3

local bottom = 0
for i = math.max(4, #m-2), #m do
  bottom = bottom + (tonumber(m[i].profit or 0) or 0)
end
bottom = bottom / 3

if top - bottom > 50 then
  vars['分支'] = 'up'
  log('info', 'Lua判断：向上分支（前3品利润明显高于后3品）')
elseif bottom - top > 50 then
  vars['分支'] = 'down'
  log('info', 'Lua判断：向下分支')
else
  vars['分支'] = 'flat'
  log('info', 'Lua判断：横盘分支')
end";
            var setBranch = b.N("set_var"); setBranch.Params["var"] = "分支结果"; setBranch.Params["value"] = "{分支}";
            var logU = b.N("log_msg"); logU.Params["text"] = "📈 向上分支：接追涨逻辑";
            var logD = b.N("log_msg"); logD.Params["text"] = "📉 向下分支：接抄底逻辑";
            var logF = b.N("log_msg"); logF.Params["text"] = "➖ 横盘：接观望/跳过";
            var end1 = b.N("end");
            var end2 = b.N("end");
            var end3 = b.N("end");

            b.L(start, "", note); b.L(note, "", fetch); b.L(fetch, "", lua); b.L(lua, "", setBranch);
            b.L(setBranch, "", logU);  // 简化：分支后先接 up
            b.L(logU, "", end1);
            b.L(logD, "", end2);
            b.L(logF, "", end3);

            return b.Finish("lua_condition_branch", "⑭ Lua条件分支三路",
                "Lua脚本判断趋势向上/向下/横盘，按结果分三路走不同流程",
                new List<string>
                {
                    "必配坐标：fetch_quotes 的交易行入口",
                    "脚本里 vars['分支'] 决定走哪条路——实际用时把 set_var 替换成真正的分支节点",
                    "可在 up/down/flat 路各接不同的买卖逻辑模板"
                });
        }

        // ================= ⑮ AI决策买入（选品后先问AI，AI点头才下单） =================
        public static Template AiSmartBuy()
        {
            var b = new B("AI决策买入");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "AI买入守门员：选品后先让AI把关（行情/利润/资金综合判断），AI回答「是」才搜索买入1份。请先在「行情与AI」页填好 DeepSeek API Key";
            var ban = b.N("ban_check");
            var enter = b.N("enter_trade");
            var tab = b.N("trade_tab"); tab.Params["tab"] = "购买"; tab.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "SMART"; pick.Params["minprofit"] = "3";
            var ai = b.N("ai_branch");
            ai.Params["question"] = "子弹{选品品名}现价{选品现价}，预期税后利润{选品预期利润}%，我的余额{余额}。综合趋势、流动性和资金仓位，现在适合买入吗？只回答是或否";
            var search = b.N("select_bullet");
            var lots = b.N("adjust_lots"); lots.Params["target"] = "1";
            var buy = b.N("buy");
            var blog = b.N("log_msg");
            blog.Params["text"] = "AI批准买入：{选品品名} ×1 @{选品现价}（AI判断：{AI判断}）";
            blog.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var nolog = b.N("log_msg");
            nolog.Params["text"] = "AI否决或无达标品种，本轮不买（AI判断：{AI判断}）"; nolog.Params["level"] = "等待";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", enter);
            b.L(ban, "hit", end);
            b.L(enter, "", tab); b.L(tab, "", wallet); b.L(wallet, "", fetch); b.L(fetch, "", pick);
            b.L(pick, "ok", ai);
            b.L(pick, "miss", nolog);
            b.L(ai, "yes", search);
            b.L(ai, "no", nolog);
            b.L(search, "", lots); b.L(lots, "", buy); b.L(buy, "", blog);
            b.L(blog, "", beep); b.L(beep, "", end);
            b.L(nolog, "", end);

            return b.Finish("ai_smart_buy", "⑮AI决策·买入守门员",
                "SMART选品后先问AI买不买（是/否），AI批准才搜索买入1份；AI当最后一道风控",
                new List<string>
                {
                    "前置条件：「行情与AI」页填好 DeepSeek API Key，点测试通过",
                    "必配坐标：交易行入口、购买页签、余额框、搜索框、结果第一项、数量−/+、买入按钮",
                    "想更严格可在选品节点提高最低利润%；AI回复原文存在 {AI判断} 变量里可随时打印"
                });
        }

        // ================= ⑯ AI持仓管家（读持仓→AI三选一处置：持有/交易行卖/军需处收） =================
        public static Template AiHoldManager()
        {
            var b = new B("AI持仓管家");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "AI持仓管家：读当前持仓行情→AI三选一处置（A继续持有 / B交易行卖出 / C军需处回收）。单独用时请把「持仓行情/指标」的持仓子弹名、买入价改成实际值";
            var ban = b.N("ban_check");
            var hold = b.N("hold_quote"); hold.Params["name"] = "{持仓品名}"; hold.Params["buyprice"] = "{持仓买入价}";
            var ai = b.N("ai_execute");
            ai.Params["prompt"] = "持仓{持仓品名}：现价{持仓现价}，买入价{持仓买入价}，浮盈{持仓浮盈}%，RSI{持仓RSI}，MA{持仓MA}。浮盈为正且RSI未超买→A继续持有；浮盈≥8%或RSI>75→B交易行卖出；深度套牢且趋势走坏→C军需处回收认赔。只回答A、B或C中的一个字母";
            var logA = b.N("log_msg");
            logA.Params["text"] = "AI建议继续持有：{持仓品名} 浮盈{持仓浮盈}%（AI判断：{AI执行}）";
            logA.Params["level"] = "等待";
            var beepA = b.N("beep"); beepA.Params["times"] = "1";

            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var ammo = b.N("goto_ammo"); ammo.Params["pos"] = "";
            var item = b.N("sell_pick_item"); item.Params["box"] = "";
            var chan = b.N("sell_channel"); chan.Params["channel"] = "交易行"; chan.Params["tradePos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "直接输入价"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = "";
            var qty = b.N("set_sell_qty");
            var list = b.N("list_confirm"); list.Params["pos"] = ""; list.Params["confirm"] = "";
            var logB = b.N("log_msg");
            logB.Params["text"] = "AI批准卖出：已按{持仓卖出价}上架（AI判断：{AI执行}）";
            logB.Params["level"] = "动作";
            var beepB = b.N("beep"); beepB.Params["times"] = "2";

            var openWh = b.N("open_warehouse"); openWh.Params["pos"] = "";
            var recycle = b.N("recycle_batch");
            recycle.Params["cart"] = ""; recycle.Params["selectall"] = "";
            recycle.Params["recycle"] = ""; recycle.Params["confirm"] = "";
            var logC = b.N("log_msg");
            logC.Params["text"] = "AI判定认赔：已走军需处批量回收（AI判断：{AI执行}）";
            logC.Params["level"] = "动作";

            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", hold);
            b.L(ban, "hit", end);
            b.L(hold, "", ai);
            b.L(ai, "a", logA); b.L(logA, "", beepA); b.L(beepA, "", end);
            b.L(ai, "b", sellTab);
            b.L(sellTab, "", ammo); b.L(ammo, "", item); b.L(item, "", chan);
            b.L(chan, "", price); b.L(price, "", qty); b.L(qty, "", list);
            b.L(list, "", logB); b.L(logB, "", beepB); b.L(beepB, "", end);
            b.L(ai, "c", openWh);
            b.L(openWh, "", recycle); b.L(recycle, "", logC); b.L(logC, "", end);

            return b.Finish("ai_hold_manager", "⑯AI持仓管家·三选一处置",
                "读持仓行情后让AI拍板：A继续持有 / B交易行按卖出价上架 / C军需处认赔回收，单次执行",
                new List<string>
                {
                    "单独运行：把「持仓行情/指标」的持仓子弹名填实际名称（如 .357 FMJ 500），买入价填实际成交价；或从选品/买入流程接续",
                    "AI处置规则写在「AI三选一执行」的提问里，可自定义（如加仓线/清仓线）",
                    "想持续托管：外面包一层无限循环（循环次数0），或用流程运行台的定时任务反复启动"
                });
        }

        // ================= ⑰ AI量化全自动闭环（选品AI把关 + 持仓AI三选一，999轮） =================
        public static Template AiQuantFull()
        {
            var b = new B("AI量化全自动闭环");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var guide = b.N("note");
            guide.Params["text"] = "AI量化旗舰：999轮闭环。买入前AI是/否把关，持仓期AI每20秒三选一决策（持有/止盈卖出/止损离场）。请先填好 DeepSeek API Key 并空跑确认坐标，F12急停";
            var ban1 = b.N("ban_check");

            var loop = b.N("loop_n"); loop.Params["count"] = "999";
            var mail = b.N("claim_mail");
            var enter = b.N("enter_trade");
            var tabBuy = b.N("trade_tab"); tabBuy.Params["tab"] = "购买"; tabBuy.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "SMART"; pick.Params["minprofit"] = "3";

            var aiBuy = b.N("ai_branch");
            aiBuy.Params["question"] = "准备买入子弹{选品品名}：现价{选品现价}，预期税后利润{选品预期利润}%，余额{余额}。考虑趋势、流动性与仓位管理，现在买入合适吗？只回答是或否";
            var waitMiss = b.N("delay"); waitMiss.Params["sec"] = "15";

            var searchB = b.N("select_bullet");
            var lotsB = b.N("adjust_lots");
            var buy = b.N("buy");
            var settleBuy = b.N("delay"); settleBuy.Params["sec"] = "2";

            var holdLoop = b.N("loop_while"); holdLoop.Params["expr"] = "1 == 1";
            var ban2 = b.N("ban_check");
            var hold = b.N("hold_quote");
            var aiHold = b.N("ai_execute");
            aiHold.Params["prompt"] = "持仓{选品品名}：现价{持仓现价}，买入价{选品现价}，浮盈{持仓浮盈}%，RSI{持仓RSI}，MA{持仓MA}。规则：浮盈≥8%或RSI>75→B卖出止盈；深套超过-5%且趋势走坏→C止损离场；其余→A继续持有。只回答A、B或C中的一个字母";
            var tick = b.N("delay"); tick.Params["sec"] = "20";

            var sellTab = b.N("trade_tab"); sellTab.Params["tab"] = "出售"; sellTab.Params["pos"] = "";
            var ammo = b.N("goto_ammo"); ammo.Params["pos"] = "";
            var item = b.N("sell_pick_item"); item.Params["box"] = "";
            var chan = b.N("sell_channel"); chan.Params["channel"] = "交易行"; chan.Params["tradePos"] = "";
            var price = b.N("set_price");
            price.Params["mode"] = "直接输入价"; price.Params["price"] = "{持仓卖出价}";
            price.Params["box"] = ""; price.Params["bar"] = "";
            var qty = b.N("set_sell_qty");
            var listOk = b.N("list_confirm"); listOk.Params["pos"] = ""; listOk.Params["confirm"] = "";
            var listed = b.N("log_msg");
            listed.Params["text"] = "AI决策卖出：已按{持仓卖出价}上架{选品品名}×{选品份数}（AI判断：{AI执行}），进入下一轮";
            listed.Params["level"] = "动作";
            var listBeep = b.N("beep"); listBeep.Params["times"] = "1";
            var brk = b.N("break");

            var errBeep = b.N("beep"); errBeep.Params["times"] = "3";
            var errStop = b.N("stop_here");
            errStop.Params["text"] = "检测到封禁/异常弹窗，已紧急停止，请立即人工检查账号！";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", guide); b.L(guide, "", ban1);
            b.L(ban1, "safe", loop);
            b.L(ban1, "hit", errBeep); b.L(errBeep, "", errStop);

            b.L(loop, "body", mail);
            b.L(mail, "", enter); b.L(enter, "", tabBuy); b.L(tabBuy, "", wallet);
            b.L(wallet, "", fetch); b.L(fetch, "", pick);
            b.L(pick, "ok", aiBuy);
            b.L(pick, "miss", waitMiss);
            b.L(aiBuy, "yes", searchB);
            b.L(aiBuy, "no", waitMiss);
            b.L(searchB, "", lotsB); b.L(lotsB, "", buy); b.L(buy, "", settleBuy); b.L(settleBuy, "", holdLoop);

            b.L(holdLoop, "body", ban2);
            b.L(ban2, "safe", hold);
            b.L(ban2, "hit", errBeep);
            b.L(hold, "", aiHold);
            b.L(aiHold, "a", tick);            // 持有：20秒后隐式回流盯盘循环
            b.L(aiHold, "b", sellTab);
            b.L(aiHold, "c", sellTab);
            b.L(sellTab, "", ammo); b.L(ammo, "", item); b.L(item, "", chan);
            b.L(chan, "", price); b.L(price, "", qty); b.L(qty, "", listOk);
            b.L(listOk, "", listed); b.L(listed, "", listBeep); b.L(listBeep, "", brk);

            b.L(holdLoop, "done", loop);
            b.L(loop, "done", end);

            return b.Finish("ai_quant_full", "⑰AI量化·全自动闭环旗舰",
                "999轮：选品→AI是/否买入把关→买入→盯盘→AI三选一（持有/止盈/止损）→上架卖出，全AI决策",
                new List<string>
                {
                    "必须先在「行情与AI」页配置 DeepSeek API Key；AI不可用时流程会在AI节点报错暂停（不盲目下单）",
                    "坐标清单与⑦完整交易一致：邮箱3项、交易行入口、购买/出售页签、余额框、搜索框/结果/数量−+/买入、弹药分类/右侧子弹/弹窗交易行/价格框/上架/二次确认",
                    "AI买卖规则都写在两个AI节点的提问文本里，可改成你自己的交易纪律；建议白天先跑一轮验证再挂夜"
                });
        }

        // ================= ⑱ 六策略轮测（SMART/PROFIT/RISE/DIP/REBOUND/RANGE 同盘对比 + Python报告） =================
        public static Template MultiStrategyTest()
        {
            var b = new B("六策略轮测对比");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "多策略回测台：同一份行情分别用六大策略各选品一次，Python汇总利润榜并选出本轮最佳策略。纯网络+脚本，不操作游戏；循环3轮取样";
            var loop = b.N("loop_n"); loop.Params["count"] = "3";
            var fetch = b.N("fetch_quotes");

            var pickA = b.N("pick"); pickA.Params["strategy"] = "SMART"; pickA.Params["prefix"] = "策A"; pickA.Params["minprofit"] = "3";
            var pickB = b.N("pick"); pickB.Params["strategy"] = "PROFIT"; pickB.Params["prefix"] = "策B"; pickB.Params["minprofit"] = "3";
            var pickC = b.N("pick"); pickC.Params["strategy"] = "RISE"; pickC.Params["prefix"] = "策C"; pickC.Params["minprofit"] = "3";
            var pickD = b.N("pick"); pickD.Params["strategy"] = "DIP"; pickD.Params["prefix"] = "策D"; pickD.Params["minprofit"] = "3";
            var pickE = b.N("pick"); pickE.Params["strategy"] = "REBOUND"; pickE.Params["prefix"] = "策E"; pickE.Params["minprofit"] = "3";
            var pickF = b.N("pick"); pickF.Params["strategy"] = "RANGE"; pickF.Params["prefix"] = "策F"; pickF.Params["minprofit"] = "3";

            var py = b.N("python_script");
            py.Params["python"] = "python";
            py.Params["code"] = @"# 六策略利润榜汇总：读取 策A~策F 的选品结果，排序并选出本轮最佳
import json, os

inp = json.loads(os.environ.get('__INPUT__', '{}'))
v = inp.get('vars', {})

rows = []
for tag, label in [('策A','SMART'),('策B','PROFIT'),('策C','RISE'),('策D','DIP'),('策E','REBOUND'),('策F','RANGE')]:
    name = v.get(tag + '品名', '')
    profit = v.get(tag + '预期利润', '0')
    try:
        profit = round(float(profit), 2)
    except Exception:
        profit = 0.0
    if name:
        rows.append({'strategy': label, 'name': name, 'profit': profit})

rows.sort(key=lambda r: -r['profit'])
v['策略榜'] = json.dumps(rows, ensure_ascii=False)
if rows:
    v['最佳策略'] = rows[0]['strategy']
    v['最佳品种'] = rows[0]['name']
    v['最佳利润'] = rows[0]['profit']
else:
    v['最佳策略'] = '无'
    v['最佳品种'] = ''
    v['最佳利润'] = 0
print(json.dumps({'vars': v}, ensure_ascii=False))";

            var log1 = b.N("log_msg");
            log1.Params["text"] = "📋 本轮最佳：{最佳策略} 选 {最佳品种} 预期利润{最佳利润}%（完整榜单见变量「策略榜」）";
            log1.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "1";
            var wait = b.N("delay"); wait.Params["sec"] = "60";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", loop);
            b.L(loop, "body", fetch);
            b.L(fetch, "", pickA);
            b.L(pickA, "ok", pickB); b.L(pickA, "miss", pickB);
            b.L(pickB, "ok", pickC); b.L(pickB, "miss", pickC);
            b.L(pickC, "ok", pickD); b.L(pickC, "miss", pickD);
            b.L(pickD, "ok", pickE); b.L(pickD, "miss", pickE);
            b.L(pickE, "ok", pickF); b.L(pickE, "miss", pickF);
            b.L(pickF, "ok", py); b.L(pickF, "miss", py);
            b.L(py, "", log1); b.L(log1, "", beep); b.L(beep, "", wait);
            // wait 无出边：隐式回流 loop 头，共3轮取样
            b.L(loop, "done", end);

            return b.Finish("multi_strategy_test", "⑱六策略轮测·Python报告",
                "同一行情跑六大策略各选品一次，Python汇总利润榜、选本轮最佳策略；3轮取样不操作游戏",
                new List<string>
                {
                    "无需游戏坐标，联网即可；需本机装有 python（节点参数可填完整路径）",
                    "六路选品结果存在 策A~策F 前缀变量里；「策略榜」变量是排序后的JSON榜单",
                    "看3轮日志哪路策略经常第一，就把实盘「自动选品」节点的策略改成它"
                });
        }

        // ================= ⑲ Lua动态仓位·风控买入（脚本算份数，敞口可控） =================
        public static Template LuaPositionSizing()
        {
            var b = new B("Lua动态仓位风控");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "脚本仓位管理：选品后由 Lua 按余额和预期利润动态计算买入份数（单笔敞口10%~30%余额，上限5份），再下单。改脚本即可换任何资金管理规则";
            var ban = b.N("ban_check");
            var enter = b.N("enter_trade");
            var tab = b.N("trade_tab"); tab.Params["tab"] = "购买"; tab.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "SMART"; pick.Params["minprofit"] = "3";
            var lua = b.N("lua_script");
            lua.Params["code"] = @"-- 动态仓位：敞口 = 余额 × (10% + 利润×1%)，利润越高敞口越大，封顶30%
local bal = tonumber(vars['余额'] or 0) or 0
local price = tonumber(vars['选品现价'] or 0) or 0
local profit = tonumber(vars['选品预期利润'] or 0) or 0

if price <= 0 or bal <= 0 then
  vars['目标份数'] = 0
  log('warn', '余额或价格无效，本轮不下单')
  return
end

local risk = bal * (0.10 + math.min(profit, 20) * 0.01)
local lots = math.floor(risk / price)
if lots < 1 then lots = 1 end
if lots > 5 then lots = 5 end

vars['目标份数'] = lots
log('info', string.format('Lua仓位：余额%.0f 单价%.0f 敞口%.0f → 买入%d份', bal, price, risk, lots))";
            var ifLots = b.N("if"); ifLots.Params["expr"] = "目标份数 >= 1";
            var search = b.N("select_bullet");
            var lots = b.N("adjust_lots"); lots.Params["target"] = "{目标份数}";
            var buy = b.N("buy");
            var blog = b.N("log_msg");
            blog.Params["text"] = "已按脚本仓位买入 {选品品名} ×{目标份数} @{选品现价}";
            blog.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var nolog = b.N("log_msg");
            nolog.Params["text"] = "无达标品种或仓位计算为0，未买入"; nolog.Params["level"] = "等待";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", enter);
            b.L(ban, "hit", end);
            b.L(enter, "", tab); b.L(tab, "", wallet); b.L(wallet, "", fetch); b.L(fetch, "", pick);
            b.L(pick, "ok", lua);
            b.L(pick, "miss", nolog);
            b.L(lua, "", ifLots);
            b.L(ifLots, "true", search);
            b.L(ifLots, "false", nolog);
            b.L(search, "", lots); b.L(lots, "", buy); b.L(buy, "", blog);
            b.L(blog, "", beep); b.L(beep, "", end);
            b.L(nolog, "", end);

            return b.Finish("lua_position_sizing", "⑲Lua动态仓位·风控买入",
                "Lua按余额与预期利润算仓位（敞口10%~30%、上限5份）再买入；资金管理规则全在脚本里可改",
                new List<string>
                {
                    "必配坐标：交易行入口、购买页签、余额框、搜索框、结果第一项、数量−/+、买入按钮",
                    "改敞口比例/上限份数：直接编辑Lua脚本里的 0.10、0.01、lots>5 三处",
                    "买入后可接⑯AI持仓管家或⑩秒卖补货管理持仓"
                });
        }

        // ================= ⑳ 多品种分仓·三路建仓（SMART/DIP/RISE 各选一颗各买一份） =================
        public static Template MultiItemPortfolio()
        {
            var b = new B("多品种三路建仓");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "组合建仓：一份行情跑三路策略（SMART稳、DIP抄底、RISE追涨）各选一颗不同品种，各买1份，一次持有3颗子弹分散风险。某路无达标品种自动跳过";
            var ban = b.N("ban_check");
            var enter = b.N("enter_trade");
            var tab = b.N("trade_tab"); tab.Params["tab"] = "购买"; tab.Params["pos"] = "";
            var wallet = b.N("read_wallet"); wallet.Params["rect"] = "";
            var fetch = b.N("fetch_quotes");

            var pickA = b.N("pick"); pickA.Params["strategy"] = "SMART"; pickA.Params["prefix"] = "仓A"; pickA.Params["minprofit"] = "3";
            var ifA = b.N("if"); ifA.Params["expr"] = "仓A预期利润 >= 3";
            var searchA = b.N("select_bullet"); searchA.Params["name"] = "{仓A品名}";
            var lotsA = b.N("adjust_lots"); lotsA.Params["target"] = "1";
            var buyA = b.N("buy");
            var settleA = b.N("delay"); settleA.Params["sec"] = "2";

            var pickB = b.N("pick"); pickB.Params["strategy"] = "DIP"; pickB.Params["prefix"] = "仓B"; pickB.Params["minprofit"] = "4";
            var ifB = b.N("if"); ifB.Params["expr"] = "仓B预期利润 >= 4";
            var searchB = b.N("select_bullet"); searchB.Params["name"] = "{仓B品名}";
            var lotsB = b.N("adjust_lots"); lotsB.Params["target"] = "1";
            var buyB = b.N("buy");
            var settleB = b.N("delay"); settleB.Params["sec"] = "2";

            var pickC = b.N("pick"); pickC.Params["strategy"] = "RISE"; pickC.Params["prefix"] = "仓C"; pickC.Params["minprofit"] = "3";
            var ifC = b.N("if"); ifC.Params["expr"] = "仓C预期利润 >= 3";
            var searchC = b.N("select_bullet"); searchC.Params["name"] = "{仓C品名}";
            var lotsC = b.N("adjust_lots"); lotsC.Params["target"] = "1";
            var buyC = b.N("buy");
            var settleC = b.N("delay"); settleC.Params["sec"] = "2";

            var sumLog = b.N("log_msg");
            sumLog.Params["text"] = "本轮建仓汇总：A路 {仓A品名}｜B路 {仓B品名}｜C路 {仓C品名}（空=该路未达标）。持仓管理请接⑯AI持仓管家或⑩秒卖补货";
            sumLog.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", enter);
            b.L(ban, "hit", end);
            b.L(enter, "", tab); b.L(tab, "", wallet); b.L(wallet, "", fetch);

            b.L(fetch, "", pickA);
            b.L(pickA, "ok", ifA); b.L(pickA, "miss", pickB);
            b.L(ifA, "true", searchA); b.L(ifA, "false", pickB);
            b.L(searchA, "", lotsA); b.L(lotsA, "", buyA); b.L(buyA, "", settleA); b.L(settleA, "", pickB);

            b.L(pickB, "ok", ifB); b.L(pickB, "miss", pickC);
            b.L(ifB, "true", searchB); b.L(ifB, "false", pickC);
            b.L(searchB, "", lotsB); b.L(lotsB, "", buyB); b.L(buyB, "", settleB); b.L(settleB, "", pickC);

            b.L(pickC, "ok", ifC); b.L(pickC, "miss", sumLog);
            b.L(ifC, "true", searchC); b.L(ifC, "false", sumLog);
            b.L(searchC, "", lotsC); b.L(lotsC, "", buyC); b.L(buyC, "", settleC); b.L(settleC, "", sumLog);
            b.L(sumLog, "", beep); b.L(beep, "", end);

            return b.Finish("multi_item_portfolio", "⑳多品种分仓·三路建仓",
                "一次行情三路策略各选各买：SMART+DIP+RISE 最多持有3颗不同子弹，分散单品种风险",
                new List<string>
                {
                    "必配坐标与买入链一致：交易行入口、购买页签、余额框、搜索框、结果第一项、数量−/+、买入按钮",
                    "三路各自门槛：A路3%/B路4%(抄底更严)/C路3%，在各「如果」节点和选品节点改",
                    "建仓后卖出：用⑯AI持仓管家逐颗处置，或⑩秒卖补货周转；买第二路前会等2秒防连点"
                });
        }

        // ================= ㉑ AI盯盘·智能播报（AI点评行情+判断机会，只播报不下单） =================
        public static Template AiWatch()
        {
            var b = new B("AI盯盘智能播报");
            var start = b.N("start");
            var bring = b.N("bringfront");
            var note = b.N("note");
            note.Params["text"] = "AI盯盘台：每60秒拉一次行情，AI 分析全盘趋势并点评，再判断当下有没有值得进场的机会——有机会响铃+打印AI点评，没机会打印观望理由。只动嘴不动手，绝不买卖。需在「行情与AI」页填好 DeepSeek API Key";
            var ban = b.N("ban_check");
            var loop = b.N("loop_n"); loop.Params["count"] = "0";
            var fetch = b.N("fetch_quotes");
            var pick = b.N("pick"); pick.Params["strategy"] = "SMART"; pick.Params["minprofit"] = "3";

            var aiAsk = b.N("ai_ask");
            aiAsk.Params["prompt"] = "你是量化盯盘助手。当前全盘行情列表见数据，本轮SMART选品结果：{选品品名} 现价{选品现价} 预期税后利润{选品预期利润}%。请用两句话点评：第一句说全盘冷热和资金方向，第二句对这个选品给出短评（趋势/风险/时机）。";
            aiAsk.Params["var"] = "AI点评";

            var aiJudge = b.N("ai_branch");
            aiJudge.Params["question"] = "基于当前行情和选品结果（{选品品名} 预期利润{选品预期利润}%），现在是否存在值得人工进场买入的机会？只回答是或否";

            var hitLog = b.N("log_msg");
            hitLog.Params["text"] = "🔔 AI发现机会：{选品品名} 现价{选品现价} 利润{选品预期利润}%\nAI点评：{AI点评}\n→ 请人工进场操作";
            hitLog.Params["level"] = "动作";
            var beep = b.N("beep"); beep.Params["times"] = "2";

            var missLog = b.N("log_msg");
            missLog.Params["text"] = "👀 AI观望：{AI点评}";
            missLog.Params["level"] = "等待";

            var wait = b.N("delay"); wait.Params["sec"] = "60";
            var end = b.N("end");

            b.L(start, "", bring); b.L(bring, "", note); b.L(note, "", ban);
            b.L(ban, "safe", loop);
            b.L(ban, "hit", end);
            b.L(loop, "body", fetch);
            b.L(fetch, "", pick);
            b.L(pick, "ok", aiAsk);
            b.L(pick, "miss", wait);
            b.L(aiAsk, "", aiJudge);
            b.L(aiJudge, "yes", hitLog);
            b.L(aiJudge, "no", missLog);
            b.L(hitLog, "", beep); b.L(beep, "", wait);
            b.L(missLog, "", wait);
            // wait 无出边：隐式回流 loop 头，count=0 无限盯盘
            b.L(loop, "done", end);

            return b.Finish("ai_watch", "㉑AI盯盘·智能播报",
                "每60秒拉行情→AI点评全盘趋势→AI判断有无进场机会：有机会响铃+打印点评，无机会打印观望理由，绝不买卖",
                new List<string>
                {
                    "前置条件：「行情与AI」页填好 DeepSeek API Key；无需任何游戏坐标，联网就能跑",
                    "AI点评原文存在 {AI点评} 变量里；想改播报频率改「延时等待」的60秒，改选品门槛改选品节点",
                    "听见两声铃+看到AI点评再手动进场；上班/夜间蹲机会神器，F12随时停止"
                });
        }
    }
}

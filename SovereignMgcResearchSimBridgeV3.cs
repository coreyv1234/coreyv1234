// Range-target/capital-preservation bridge version3 callback-state wire, isolated build. SIM101 ONLY. Current red-context model.
// No saved historical order tape. Python generates signals from closed candles.
// Native compilation/fills must be checked in Windows NinjaTrader; not verified in cloud.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Timers;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class SovereignMgcResearchSimBridgeV3 : Strategy
    {
        private const string StateHeader = "version;run_id;updated_utc;phase;account;instrument;blocked;reason;last_bar_utc;last;bid;ask;quote_utc;quantity;direction;average_entry;active_id;stop;target;entry_working;realized_net;session_open_cash;highwater;session;session_count;fee;slip;initial_risk;orders_working;flatten_ct;policy_id;max_contracts;risk_limit;protected_room;risk_fraction;evaluation_enabled;goal_balance;goal_reached;first_fill_utc;entry_executed;exit_executed;entry_reported_filled;entry_terminal;entry_terminal_filled;submitted_quantity;pending_exit_callbacks;ledger_uncertain;account_signed_quantity";
        private const string CommandHeader = "version;run_id;command_id;issued_utc;expires_utc;kind;model_id;direction;quantity;reference;atr;stop;target;planned_risk;expected_entry;budget_ceiling";
        private readonly Dictionary<string, Order> orders = new Dictionary<string, Order>();
        private readonly Dictionary<string, int> exitReportedByOrder = new Dictionary<string, int>();
        private readonly HashSet<string> commandIds = new HashSet<string>(), modelIds = new HashSet<string>(), executionIds = new HashSet<string>();
        private StreamWriter minutes, journal;
        private Timer timer;
        private TimeZoneInfo chicago;
        private string runId, runFolder, activeId = "", tag = "", session = "", reason = "";
        private int actualQuantity, direction, count;
        private double averageEntry, stop, target, initialRisk, realizedNet, sessionCash = 50000, highWater = 50000;
        private DateTime lastBar = DateTime.MinValue, lastBid = DateTime.MinValue, lastAsk = DateTime.MinValue, started, entryStarted, lastFill;
        private bool blocked, dailyBlocked, entryRequested, flattenRequested, flattenFailed, endOfDayMarked, realtime, disposing, hadController, auditFailed;
        private Order entryOrder;
        private const string PolicyId = "MGC_RED_CONTEXT800_SIM_20261106";
        private const string BridgeBuild = "20261106SIM3";
        private readonly HashSet<string> ownedTags = new HashSet<string>();
        private volatile bool foreignActivity;
        private FileStream ledgerLock;
        private string ledgerPath;
        private double approvedBudget;
        private bool goalReached;
        private bool ledgerInitialized;
        private bool ledgerUncertain, entryTerminal, entryTerminalFilled;
        private int submittedQuantity, entryReportedFilled, entryExecuted, exitExecuted;
        private DateTime lastOrderEvent = DateTime.MinValue;
        private DateTime firstEntryFillUtc = DateTime.MinValue;

        [NinjaScriptProperty]
        [Display(Name="Read launcher startup configuration", GroupName="Bridge", Order=4)]
        public bool ReadStartupFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Exact account name", GroupName="Account", Order=1)]
        public string ExpectedAccountName { get; set; }
        [NinjaScriptProperty]
        [Display(Name="Must stay false: Sim101 only", GroupName="Account", Order=2)]
        public bool EnableEvaluationAccount { get; set; }
        [NinjaScriptProperty]
        [Range(1,10)]
        [Display(Name="Maximum MGC contracts (match Python config)", GroupName="Account", Order=3)]
        public int MaximumContracts { get; set; }
        [NinjaScriptProperty]
        [Range(0.0,10000000.0)]
        [Display(Name="Initial current balance", GroupName="Initial account state", Order=1)]
        public double InitialCash { get; set; }
        [NinjaScriptProperty]
        [Range(0.0,10000000.0)]
        [Display(Name="Initial trading-day opening balance", GroupName="Initial account state", Order=2)]
        public double InitialSessionCash { get; set; }
        [NinjaScriptProperty]
        [Range(0.0,10000000.0)]
        [Display(Name="Initial trailing high-water mark", GroupName="Initial account state", Order=3)]
        public double InitialHighWater { get; set; }
        [NinjaScriptProperty]
        [Display(Name="Initial CME session yyyy-MM-dd", GroupName="Initial account state", Order=4)]
        public string InitialStateSession { get; set; }
        [NinjaScriptProperty]
        [Display(Name="Reconcile saved ledger from entered account state", GroupName="Initial account state", Order=5)]
        public bool ReconcileAtStartup { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Bridge directory", GroupName="Bridge", Order=1)]
        public string BridgeDirectory { get; set; }

        [NinjaScriptProperty]
        [Range(0.0,100.0)]
        [Display(Name="Assumed round-turn MGC fee", GroupName="Bridge", Order=2)]
        public double AssumedRoundTurnFee { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Close all positions at noon (otherwise 15:55)", GroupName="Bridge", Order=3)]
        public bool CloseAtNoon { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SovereignMgcResearchSimBridgeV3";
                Description = "Independent range TP and red-context800 risk cap. Exact account; persisted account ledger. Native validation required.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                StartBehavior = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 1;
                Slippage = 1;
                AssumedRoundTurnFee = 2.8;
                CloseAtNoon = false;
                BridgeDirectory = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SovereignResearchSimV3");
                ExpectedAccountName = "Sim101"; EnableEvaluationAccount = false; MaximumContracts = 10;
                InitialCash = 0; InitialSessionCash = 0; InitialHighWater = 0; InitialStateSession = ""; ReconcileAtStartup = false;
                ReadStartupFile = true;
            }
            else if (State == State.DataLoaded)
            {
                ledgerInitialized = false; ledgerPath = null;
                if (!ReadStartupFile) throw new InvalidOperationException("Read launcher startup configuration must stay enabled.");
                LoadStartupConfig();
                if (!SafeAccount()) throw new InvalidOperationException("Sim101 account/simulation mode does not match the strategy configuration.");
                if (CloseAtNoon || MaximumContracts < 1 || MaximumContracts > 10) throw new InvalidOperationException("Use noon entry cutoff/15:55 safety close; max contracts must match Python config.");
                if (Calculate != Calculate.OnBarClose || Instrument.MasterInstrument.Name != "MGC" || Math.Abs(Instrument.MasterInstrument.PointValue-10) > 0.000001 || Math.Abs(TickSize-.1) > 0.000001 || BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                    throw new InvalidOperationException("Use one-minute bars on an actual MGC expiry; configure DoNotMerge.");
                lock (Account.Positions)
                    foreach (NinjaTrader.Cbi.Position p in Account.Positions)
                        if (p.MarketPosition != MarketPosition.Flat && p.Quantity != 0)
                            throw new InvalidOperationException("Start flat with no other account positions; account risk is shared across instruments.");
                lock (Account.Orders)
                    foreach (Order o in Account.Orders)
                        if (!Terminal(o.OrderState))
                            throw new InvalidOperationException("Resolve existing MGC orders before enabling the bridge.");
                // Reset runtime order state; financial state is loaded separately
                // from the persistent account ledger below, never silently reset.
                orders.Clear(); commandIds.Clear(); modelIds.Clear(); executionIds.Clear(); ownedTags.Clear(); foreignActivity = false;
                exitReportedByOrder.Clear(); exitExecuted = 0;
                activeId = ""; tag = ""; session = ""; reason = "";
                actualQuantity = 0; direction = 0; count = 0; approvedBudget = 0; goalReached = false;
                ledgerUncertain = false; entryTerminal = false; entryTerminalFilled = false;
                submittedQuantity = 0; entryReportedFilled = 0; entryExecuted = 0; lastOrderEvent = DateTime.MinValue; firstEntryFillUtc = DateTime.MinValue;
                averageEntry = 0; stop = 0; target = 0; initialRisk = 0; realizedNet = 0;
                sessionCash = 50000; highWater = 50000;
                lastBar = DateTime.MinValue; lastBid = DateTime.MinValue; lastAsk = DateTime.MinValue; lastFill = DateTime.MinValue;
                blocked = false; dailyBlocked = false; entryRequested = false; flattenRequested = false; flattenFailed = false;
                endOfDayMarked = false; realtime = false; disposing = false; hadController = false; auditFailed = false; entryOrder = null;
                chicago = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                LoadLedger();
                if (ReconcileAtStartup)
                {
                    string startup = Path.Combine(BridgeDirectory,"startup_account.txt");
                    if (File.Exists(startup))
                    {
                        string[] lines = File.ReadAllLines(startup);
                        string[] fields = lines[1].Split(';'); fields[8] = "0";
                        Atomic(startup,lines[0]+Environment.NewLine+String.Join(";",fields)+Environment.NewLine);
                    }
                    ReconcileAtStartup = false;
                }
                Account.ExecutionUpdate += AccountExecution;
                runId = Guid.NewGuid().ToString("N");
                runFolder = Path.Combine(BridgeDirectory, "run_"+runId);
                Directory.CreateDirectory(Path.Combine(runFolder,"commands"));
                Directory.CreateDirectory(Path.Combine(runFolder,"processed"));
                minutes = new StreamWriter(new FileStream(Path.Combine(runFolder,"minutes.csv"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8) { AutoFlush = true };
                minutes.WriteLine("time_utc,instrument,open,high,low,close,volume");
                journal = new StreamWriter(new FileStream(Path.Combine(runFolder,"executions.csv"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),Encoding.UTF8) { AutoFlush = true };
                journal.WriteLine("time_utc,event,model_id,quantity,price,cumulative_net,detail");
                Atomic(Path.Combine(BridgeDirectory,"active_run.txt"),runId+Environment.NewLine);
                Log("CREATED",0,AssumedRoundTurnFee,"build="+BridgeBuild+"|Persistent account ledger loaded; cash="+Num(50000+realizedNet)+"|highwater="+Num(highWater)+"|fees assumed");
                Print("MGC range bridge: "+runFolder);
            }
            else if (State == State.Realtime)
            {
                if (!SafeAccount()) throw new InvalidOperationException("Exact account guard changed.");
                realtime = true; started = DateTime.UtcNow;
                RollSession(started);
                timer = new Timer(250); timer.AutoReset = false;
                timer.Elapsed += TimerElapsed;
                timer.Start();
                Log("REALTIME",0,0,"Waiting for the Python controller heartbeat and fresh bid/ask");
                Publish();
            }
            else if (State == State.Terminated)
            {
                disposing = true; realtime = false;
                if (Account != null) Account.ExecutionUpdate -= AccountExecution;
                try { if (ledgerInitialized) PersistLedger(); } catch { Print("Account ledger could not be saved; reconcile before restart."); }
                if (ledgerLock != null) { ledgerLock.Dispose(); ledgerLock = null; }
                if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
                if (journal != null) { Log("TERMINATED",actualQuantity,0,"Native realtime error handling remains enabled; reconcile in Orders/Positions"); journal.Dispose(); journal = null; }
                if (minutes != null) { minutes.Dispose(); minutes = null; }
            }
        }

        private bool SafeAccount() { return Account != null && Account.Name == "Sim101" && ExpectedAccountName == "Sim101" && !EnableEvaluationAccount; }
        private void LoadStartupConfig()
        {
            string path = Path.Combine(BridgeDirectory,"startup_account.txt");
            if (!File.Exists(path)) throw new InvalidOperationException("Start START_SIM.ps1 first to write the simulation startup file.");
            string[] lines = File.ReadAllLines(path);
            const string header = "version;account;mode;max_contracts;cash;session_cash;highwater;session;reconcile";
            if (lines.Length != 2 || lines[0] != header) throw new InvalidDataException("Wrong launcher startup configuration.");
            string[] v = lines[1].Split(';');
            if (v.Length != 9 || v[0] != "2" || (v[2] != "simulation" || v[1] != "Sim101") || (v[8] != "0" && v[8] != "1"))
                throw new InvalidDataException("Invalid launcher account configuration.");
            ExpectedAccountName = v[1]; EnableEvaluationAccount = v[2] == "evaluation";
            MaximumContracts = int.Parse(v[3],CultureInfo.InvariantCulture);
            InitialCash = Number(v[4]); InitialSessionCash = Number(v[5]); InitialHighWater = Number(v[6]);
            InitialStateSession = v[7]; ReconcileAtStartup = v[8] == "1";
        }
        private string Hash(string text)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();
        }
        private string SessionKey(DateTime now)
        {
            DateTime ct = TimeZoneInfo.ConvertTimeFromUtc(now,chicago);
            return ct.AddDays(ct.Hour >= 17 ? 1 : 0).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        }
        private double CapitalBudget()
        {
            return Math.Max(0,Math.Min(800,50000+realizedNet-Floor()));
        }
        private bool Owns(Order order)
        {
            lock (ownedTags)
                return order != null && (ownedTags.Contains(order.Name ?? "") || ownedTags.Contains(order.FromEntrySignal ?? ""));
        }
        private void AccountExecution(object sender,ExecutionEventArgs e)
        {
            // Account callbacks can run on another thread. Native order calls
            // remain on the strategy event thread through Pump().
            if (realtime && (e == null || e.Execution == null || !Owns(e.Execution.Order))) foreignActivity = true;
        }
        private bool ForeignOrdersOrPositions()
        {
            lock (Account.Orders)
                foreach (Order o in Account.Orders)
                    if (!Terminal(o.OrderState) && !Owns(o)) return true;
            lock (Account.Positions)
                foreach (NinjaTrader.Cbi.Position p in Account.Positions)
                    if (p.Instrument.FullName != Instrument.FullName && p.MarketPosition != MarketPosition.Flat && p.Quantity != 0) return true;
            return false;
        }
        private void LoadLedger()
        {
            // Global to this NinjaTrader installation, independent of chart or
            // bridge directory, so moving the release cannot reset account room.
            string directory = Path.Combine(NinjaTrader.Core.Globals.UserDataDir,"SovereignAccountState");
            Directory.CreateDirectory(directory);
            string key = Hash(Account.Name).Substring(0,32);
            ledgerLock = new FileStream(Path.Combine(directory,key+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            ledgerPath = Path.Combine(directory,key+".research_sim800.ledger");
            if (File.Exists(ledgerPath) && !ReconcileAtStartup)
            {
                string[] lines = File.ReadAllLines(ledgerPath);
                if (lines.Length != 2 || Hash(lines[0]) != lines[1]) throw new InvalidDataException("Account ledger integrity failed; reconcile before restart.");
                string[] v = lines[0].Split(';');
                if (v.Length != 12 || v[0] != "2" || v[1] != PolicyId || Encoding.UTF8.GetString(Convert.FromBase64String(v[2])) != Account.Name)
                    throw new InvalidDataException("Wrong persisted account/profile.");
                if (v[8] != "1") throw new InvalidOperationException("Prior exposure or account activity is unresolved. Reconcile Orders/Positions and enter current dashboard values before ReconcileAtStartup.");
                if (int.Parse(v[9],CultureInfo.InvariantCulture) != MaximumContracts || Number(v[10]) != AssumedRoundTurnFee)
                    throw new InvalidOperationException("Persisted contract/fee settings changed; reconcile explicitly instead of resetting account state.");
                if (v[11] != "0" && v[11] != "1") throw new InvalidDataException("Invalid phase state");
                goalReached = v[11] == "1";
                session = v[3]; realizedNet = Number(v[4])-50000; sessionCash = Number(v[5]); highWater = Number(v[6]); count = int.Parse(v[7],CultureInfo.InvariantCulture);
                if (50000+realizedNet <= 0 || sessionCash <= 0 || highWater < 50000 || highWater > 52000 || count < 0)
                    throw new InvalidDataException("Invalid persisted capital state.");
                DateTime parsed;
                if (!DateTime.TryParseExact(session,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed) || String.CompareOrdinal(session,SessionKey(DateTime.UtcNow)) > 0)
                    throw new InvalidDataException("Invalid/future persisted trading day.");
                ledgerInitialized = true;
                return;
            }
            bool explicitState = EnableEvaluationAccount || ReconcileAtStartup || InitialCash != 0 || InitialSessionCash != 0 || InitialHighWater != 0;
            if (explicitState)
            {
                if (!(InitialCash > 0) || !(InitialSessionCash > 0) || InitialHighWater < 50000 || InitialHighWater > 52000 ||
                    InitialStateSession != SessionKey(DateTime.UtcNow) || Double.IsNaN(InitialCash) || Double.IsInfinity(InitialCash) ||
                    Double.IsNaN(InitialSessionCash) || Double.IsInfinity(InitialSessionCash) || Double.IsNaN(InitialHighWater) || Double.IsInfinity(InitialHighWater))
                    throw new InvalidOperationException("Enter current balance, trading-day opening balance, trailing high-water and current CME date "+SessionKey(DateTime.UtcNow)+" from the account dashboard.");
                realizedNet = InitialCash-50000; sessionCash = InitialSessionCash; highWater = InitialHighWater; session = InitialStateSession;
            }
            else { realizedNet = 0; sessionCash = 50000; highWater = 50000; session = SessionKey(DateTime.UtcNow); }
            if (File.Exists(ledgerPath)) File.Copy(ledgerPath,ledgerPath+".backup_"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",CultureInfo.InvariantCulture),false);
            count = 0; ledgerInitialized = true; PersistLedger();
        }
        private void PersistLedger()
        {
            if (ledgerPath == null || !ledgerInitialized) return;
            bool flat = !foreignActivity && !ledgerUncertain && actualQuantity == 0 && !EntryWorking() && !OrdersWorking();
            string body = String.Join(";",new string[] { "2",PolicyId,Convert.ToBase64String(Encoding.UTF8.GetBytes(Account.Name)),session,
                Num(50000+realizedNet),Num(sessionCash),Num(highWater),count.ToString(CultureInfo.InvariantCulture),flat ? "1" : "0",
                MaximumContracts.ToString(CultureInfo.InvariantCulture),Num(AssumedRoundTurnFee),goalReached ? "1" : "0" });
            Atomic(ledgerPath,body+Environment.NewLine+Hash(body)+Environment.NewLine);
        }
        private bool Terminal(OrderState state) { return state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected; }
        private bool Working(Order order) { return order != null && !Terminal(order.OrderState); }
        private bool EntryWorking()
        {
            // A Filled order update can precede its execution callbacks.
            // Never clear/adopt a flat trade before those fills are accounted.
            return entryRequested || (entryOrder != null && !entryTerminal) || entryReportedFilled > entryExecuted ||
                (entryTerminalFilled && entryExecuted < submittedQuantity);
        }
        private bool OrdersWorking()
        {
            foreach (Order o in orders.Values) if (Working(o)) return true;
            return false;
        }
        private int AccountQuantity()
        {
            int q = 0;
            lock (Account.Positions)
                foreach (NinjaTrader.Cbi.Position p in Account.Positions)
                    if (p.Instrument.FullName == Instrument.FullName)
                        q += p.MarketPosition == MarketPosition.Long ? p.Quantity : p.MarketPosition == MarketPosition.Short ? -p.Quantity : 0;
            return q;
        }
        private DateTime Utc(DateTime t)
        {
            return t.Kind == DateTimeKind.Utc ? t : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t,DateTimeKind.Unspecified),NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo);
        }
        private DateTime ParseTime(string s) { return DateTimeOffset.Parse(s,CultureInfo.InvariantCulture).UtcDateTime; }
        private string Iso(DateTime t) { return DateTime.SpecifyKind(t,DateTimeKind.Utc).ToString("o",CultureInfo.InvariantCulture); }
        private string Num(double x) { return x.ToString("R",CultureInfo.InvariantCulture); }
        private bool Id(string id) { return System.Text.RegularExpressions.Regex.IsMatch(id ?? "","\\A[a-f0-9]{32}\\z"); }
        private double Number(string s)
        {
            double x = double.Parse(s,NumberStyles.Float,CultureInfo.InvariantCulture);
            if (double.IsNaN(x) || double.IsInfinity(x)) throw new InvalidDataException("Nonfinite command");
            return x;
        }
        private bool OnTick(double price) { return price > 0 && Math.Abs(price/TickSize-Math.Round(price/TickSize)) < 0.000001; }
        private void Atomic(string path,string body)
        {
            string tmp = path+".tmp";
            File.WriteAllText(tmp,body,new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path);
        }
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 0 || minutes == null) return;
            DateTime stamp = Utc(Time[0]);
            if (stamp > DateTime.UtcNow) return; // An in-progress historical tail is not a completed live observation.
            if (stamp <= lastBar) { if (stamp == lastBar) return; throw new InvalidDataException("Non-monotonic chart bars"); }
            try { minutes.WriteLine(String.Join(",",new string[] { Iso(stamp),Instrument.FullName,Num(Open[0]),Num(High[0]),Num(Low[0]),Num(Close[0]),Num(Volume[0]) })); }
            catch (IOException) { if (realtime) Halt("Chart export failed"); else throw; return; }
            lastBar = stamp;
            if (realtime) Pump();
        }
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Receipt time gates entry freshness; do not interpret a delayed exchange timestamp as fresh history.
            if (e.MarketDataType == MarketDataType.Bid) lastBid = DateTime.UtcNow;
            else if (e.MarketDataType == MarketDataType.Ask) lastAsk = DateTime.UtcNow;
        }
        private void TimerElapsed(object sender,ElapsedEventArgs e)
        {
            if (disposing || !realtime) return;
            try { TriggerCustomEvent(o => { if (!disposing && realtime) { try { Pump(); } finally { if (timer != null) timer.Start(); } } },null); }
            catch { /* A terminated strategy cannot be restarted from the timer thread. */ }
        }
        private bool Heartbeat(DateTime now)
        {
            try
            {
                string[] v = File.ReadAllText(Path.Combine(runFolder,"heartbeat.txt")).Trim().Split(';');
                double age = (now-ParseTime(v[1])).TotalSeconds;
                return v.Length == 2 && v[0] == runId && age >= -1 && age <= 15;
            }
            catch { return false; }
        }
        private double Floor() { return Math.Max(highWater-2000+50,sessionCash-900+50); }
        private void RollSession(DateTime now)
        {
            DateTime ct = TimeZoneInfo.ConvertTimeFromUtc(now,chicago);
            string key = ct.AddDays(ct.Hour >= 17 ? 1 : 0).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            if (key == session) return;
            if (session.Length != 0)
            {
                if (actualQuantity != 0 || EntryWorking()) { Halt("Position survived session rollover"); return; }
                highWater = Math.Min(52000,Math.Max(highWater,50000+realizedNet));
            }
            if (session.Length != 0 && 50000+realizedNet > 53000.000001) goalReached = true;
            session = key; sessionCash = 50000+realizedNet; count = 0; dailyBlocked = false; endOfDayMarked = false;
            PersistLedger();
            Log("SESSION",0,sessionCash,key);
        }
        private void Flatten(string why)
        {
            // Cancel entry remainder so a late partial entry cannot reopen after an exit.
            if (Working(entryOrder)) CancelOrder(entryOrder);
            if (flattenRequested || flattenFailed) return;
            // A known native exit can report Filled before its execution
            // callbacks. Do not duplicate that exit during the callback gap.
            if (PendingExitCallbacks() > 0) return;
            MarketPosition nativeSide = Position.MarketPosition;
            if (ledgerUncertain && nativeSide == MarketPosition.Flat) return;
            if (actualQuantity > 0 || Position.MarketPosition != MarketPosition.Flat)
            {
                flattenRequested = true;
                string from = ledgerUncertain ? "" : tag;
                bool longSide = nativeSide == MarketPosition.Long || (nativeSide == MarketPosition.Flat && direction == 1);
                if (longSide) ExitLong("BridgeFlat",from);
                else ExitShort("BridgeFlat",from);
                Log("FLATTEN_REQUEST",actualQuantity,0,why);
            }
        }
        private bool PendingEntryCallbacks()
        {
            return entryReportedFilled > entryExecuted || (entryTerminalFilled && entryExecuted < submittedQuantity);
        }
        private int PendingExitCallbacks()
        {
            int reported = 0;
            foreach (int filled in exitReportedByOrder.Values) reported += filled;
            return Math.Max(0,reported-exitExecuted);
        }
        private void Halt(string why)
        {
            bool first = !blocked;
            blocked = true; reason = why;
            if (first) Log("HALTED",actualQuantity,0,why);
            Flatten(why);
        }
        private void Pump()
        {
            try { PumpCore(); }
            catch (Exception)
            {
                try { Halt("Bridge processing failed; reconcile native Orders/Positions"); }
                catch { blocked = true; Print("MGC bridge failed to confirm flatten submission. Inspect native Orders/Positions immediately."); }
            }
        }
        private void PumpCore()
        {
            if (!SafeAccount()) { Halt("Account guard"); return; }
            DateTime now = DateTime.UtcNow;
            if (auditFailed) Halt("Execution journal write failed");
            if (foreignActivity || ForeignOrdersOrPositions())
            { foreignActivity = true; Halt("Other account activity detected; reconcile dashboard balance and trailing floor before restart"); PersistLedger(); }
            RollSession(now);
            DateTime ct = TimeZoneInfo.ConvertTimeFromUtc(now,chicago);
            int minute = ct.Hour*60+ct.Minute;
            int cutoff = CloseAtNoon ? 720 : 955;
            if (minute >= cutoff && minute < 1020)
            {
                Flatten(CloseAtNoon ? "12:00 Chicago cutoff" : "15:55 Chicago cutoff");
                if (actualQuantity == 0 && !EntryWorking() && !endOfDayMarked)
                { highWater = Math.Min(52000,Math.Max(highWater,50000+realizedNet)); endOfDayMarked = true; PersistLedger(); }
            }
            bool heartbeat = Heartbeat(now);
            if (heartbeat) hadController = true;
            if (!heartbeat && (hadController || actualQuantity > 0 || EntryWorking())) Halt("Python heartbeat lost; restart requires a reconciled flat run");
            if (blocked) Flatten(reason);
            if (EntryWorking() && (now-entryStarted).TotalSeconds > 10) Halt("Entry acknowledgement/fill timeout");
            if (PendingExitCallbacks() > 0 && (now-lastOrderEvent).TotalSeconds > 10)
            { ledgerUncertain = true; Halt("Native exit execution callbacks missing; reconcile account state"); PersistLedger(); }
            double mark = direction == 1 ? GetCurrentBid() : GetCurrentAsk();
            if (actualQuantity > 0 && mark > 0)
            {
                double liquidation = 50000+realizedNet+(direction*(mark-averageEntry)*10-AssumedRoundTurnFee/2-1)*actualQuantity;
                if (liquidation <= Floor()) { dailyBlocked = true; Halt("Marked liquidation reached account/day reserve"); }
            }
            if (actualQuantity > 0 && !PendingEntryCallbacks() && PendingExitCallbacks() == 0 && (now-lastFill).TotalSeconds > 3) CheckProtection();
            if (!PendingEntryCallbacks() && PendingExitCallbacks() == 0 && (now-lastFill).TotalSeconds > 3 && AccountQuantity() != direction*actualQuantity)
                Halt("Native account position differs from execution ledger; reconcile manually");
            bool knownFullEntry = entryTerminalFilled && entryExecuted >= submittedQuantity;
            if (actualQuantity == 0 && !EntryWorking() && !OrdersWorking() && activeId.Length > 0 &&
                (knownFullEntry || (now-lastOrderEvent).TotalSeconds >= 3))
            { Log("TRADE_CLOSED",0,0,"Native orders terminal; cumulative net uses assumed fees"); activeId = ""; tag = ""; direction = 0; averageEntry = 0; initialRisk = 0; approvedBudget = 0; flattenRequested = false; entryOrder = null; PersistLedger(); }
            // Unlimited simulation phase latches only on a subsequent session in RollSession().
            foreach (string file in Directory.GetFiles(Path.Combine(runFolder,"commands"),"*.cmd"))
            {
                try { Handle(file,now,heartbeat); }
                catch (Exception) { Log("COMMAND_INVALID",0,0,"Malformed command; no exception payload retained"); Halt("Malformed command or bridge I/O failure"); }
                finally
                {
                    string done = Path.Combine(runFolder,"processed",Path.GetFileName(file));
                    if (File.Exists(file)) { if (File.Exists(done)) File.Delete(file); else File.Move(file,done); }
                }
            }
            Publish();
        }
        private void CheckProtection()
        {
            int stops = 0, targets = 0;
            foreach (Order o in orders.Values)
            {
                bool accepted = o.OrderState == OrderState.Accepted || o.OrderState == OrderState.Working || o.OrderState == OrderState.PartFilled;
                if (!accepted || o.FromEntrySignal != tag) continue;
                if (o.Name == "Stop loss") stops += o.Quantity-o.Filled;
                if (o.Name == "Profit target") targets += o.Quantity-o.Filled;
            }
            if (stops < actualQuantity || targets < actualQuantity) Halt("Native stop/target coverage missing after fill grace period");
        }
        private void Handle(string file,DateTime now,bool heartbeat)
        {
            string[] lines = File.ReadAllLines(file);
            if (lines.Length != 2 || lines[0] != CommandHeader) throw new InvalidDataException("Command schema");
            string[] v = lines[1].Split(';');
            if (v.Length != 16 || v[0] != "3" || !Id(v[1]) || !Id(v[2]) || !Id(v[6])) throw new InvalidDataException("Command fields");
            if (v[1] != runId) { Log("COMMAND_REJECTED",0,0,"Wrong run identity"); return; }
            if (!commandIds.Add(v[2])) { Log("DUPLICATE_COMMAND",0,0,v[2]); return; }
            DateTime issued = ParseTime(v[3]), expires = ParseTime(v[4]);
            if (issued > now.AddSeconds(1) || issued < now.AddSeconds(-5) || expires < now || expires > issued.AddSeconds(5) || expires <= issued)
            { Log("COMMAND_REJECTED",0,0,"Expired/future command"); return; }
            string kind = v[5];
            if (kind == "FLATTEN") { Halt("Controller requested flatten"); return; }
            if (blocked || ledgerUncertain || !heartbeat) { Log("COMMAND_REJECTED",0,0,"Bridge halted or controller heartbeat missing"); return; }
            int d = int.Parse(v[7],CultureInfo.InvariantCulture), q = int.Parse(v[8],CultureInfo.InvariantCulture);
            double reference = Number(v[9]), atr = Number(v[10]), proposedStop = Number(v[11]), proposedTarget = Number(v[12]), risk = Number(v[13]), expected = Number(v[14]), budgetCeiling = Number(v[15]);
            if (kind == "STOP")
            {
                if (v[6] != activeId || q != actualQuantity || d != direction || actualQuantity == 0 || EntryWorking() || PendingExitCallbacks() > 0 || ledgerUncertain)
                { Log("COMMAND_REJECTED",0,0,"Stale stop identity/quantity"); return; }
                double quote = d == 1 ? GetCurrentBid() : GetCurrentAsk();
                if (quote <= 0 || (now-lastBid).TotalSeconds > 5 || (now-lastAsk).TotalSeconds > 5 || !OnTick(proposedStop) || d*(proposedStop-stop) < TickSize-0.000001 || d*(quote-proposedStop) < TickSize-0.000001)
                { Log("COMMAND_REJECTED",0,0,"Stop must tighten and remain on the protective side"); return; }
                stop = proposedStop; SetStopLoss(tag,CalculationMode.Price,stop,false);
                Log("STOP_REQUEST",q,stop,"Effective now; never retroactive into closed input bar"); return;
            }
            if (kind != "ENTER") throw new InvalidDataException("Unsupported command");
            DateTime ct = TimeZoneInfo.ConvertTimeFromUtc(now,chicago);
            int minute = ct.Hour*60+ct.Minute;
            if (ct.DayOfWeek == DayOfWeek.Saturday || ct.DayOfWeek == DayOfWeek.Sunday || minute < 120 || minute >= 720 || dailyBlocked || (!goalReached && 50000+realizedNet-sessionCash >= 1200.0-0.000001) || count >= 20 ||
                actualQuantity != 0 || AccountQuantity() != 0 || PendingExitCallbacks() > 0 || EntryWorking() || OrdersWorking() || activeId.Length != 0)
            { Log("COMMAND_REJECTED",0,0,"Entry session/flat/order guard"); return; }
            double bid = GetCurrentBid(), ask = GetCurrentAsk();
            if ((now-lastBid).TotalSeconds > 5 || (now-lastAsk).TotalSeconds > 5 || bid <= 0 || ask < bid || ask-bid > .5+0.000001 ||
                (now-lastBar).TotalSeconds < 0 || (now-lastBar).TotalSeconds > 5)
            { Log("COMMAND_REJECTED",0,0,"Stale/wide quote or closed bar"); return; }
            if ((d != 1 && d != -1) || q < 1 || q > MaximumContracts || atr <= 0 || risk <= 0 || risk > 800.000001 || budgetCeiling < risk-0.000001 || budgetCeiling > CapitalBudget()+0.000001 || !OnTick(proposedStop) || !OnTick(proposedTarget) || !OnTick(expected))
                throw new InvalidDataException("Entry risk/geometry");
            double estimate = (d == 1 ? ask : bid)+d*TickSize;
            double perRisk = d*(estimate-proposedStop)*10+AssumedRoundTurnFee+1;
            double room = Math.Min(budgetCeiling,CapitalBudget());
            double netTargetPer = d*(proposedTarget-estimate)*10-AssumedRoundTurnFee;
            if (!goalReached && (netTargetPer <= 0 || netTargetPer*q > 1200-(50000+realizedNet-sessionCash)+0.000001))
            { Log("COMMAND_REJECTED",q,estimate,"Evaluation profit budget does not fit actual quote"); return; }
            if (d*(estimate-proposedStop) <= 0 || d*(proposedTarget-estimate) <= 0 || 
                d*(estimate-expected) > TickSize+0.000001 || perRisk*q > room+0.000001 || perRisk*q > risk+0.000001)
            { Log("COMMAND_REJECTED",q,estimate,"Quote moved, invalidation passed, or total planned risk exceeded"); return; }
            if (!modelIds.Add(v[6])) { Log("COMMAND_REJECTED",0,0,"Source signal already used"); return; }
            // Record consumption before native submission. Crash recovery never resubmits an ambiguous entry.
            activeId = v[6]; tag = "MGCR_"+activeId; lock (ownedTags) ownedTags.Add(tag); direction = d; stop = proposedStop; target = proposedTarget; initialRisk = risk; approvedBudget = room;
            submittedQuantity = q; entryReportedFilled = 0; entryExecuted = 0; entryTerminal = false; entryTerminalFilled = false;
            exitExecuted = 0; exitReportedByOrder.Clear();
            flattenRequested = false; entryRequested = true; entryStarted = now; count++;
            PersistLedger(); // uncertain until native terminal callbacks; never reset account room on restart
            Log("ENTRY_REQUEST",q,estimate,"id="+v[2]+"|stop="+Num(stop)+"|target="+Num(target)+"|risk="+Num(risk)+"|direction="+d.ToString(CultureInfo.InvariantCulture));
            if (auditFailed) { entryRequested = false; Halt("Execution journal unavailable before entry"); return; }
            SetStopLoss(tag,CalculationMode.Price,stop,false);
            SetProfitTarget(tag,CalculationMode.Price,target);
            if (d == 1) EnterLong(q,tag); else EnterShort(q,tag);
        }
        protected override void OnOrderUpdate(Order order,double limitPrice,double stopPrice,int quantity,int filled,double averageFillPrice,
            OrderState orderState,DateTime time,ErrorCode error,string nativeError)
        {
            if (order == null || journal == null) return;
            // Native IDs can be assigned after the first callback. Do not count one order twice.
            var oldKeys = new List<string>();
            string orderKey = String.IsNullOrEmpty(order.OrderId) ? order.Name+"_"+System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(order).ToString(CultureInfo.InvariantCulture) : order.OrderId;
            foreach (var pair in orders) if (Object.ReferenceEquals(pair.Value,order) && pair.Key != orderKey) oldKeys.Add(pair.Key);
            foreach (string key in oldKeys)
            {
                orders.Remove(key);
                if (exitReportedByOrder.ContainsKey(key))
                {
                    int prior = exitReportedByOrder[key]; exitReportedByOrder.Remove(key);
                    exitReportedByOrder[orderKey] = Math.Max(exitReportedByOrder.ContainsKey(orderKey) ? exitReportedByOrder[orderKey] : 0,prior);
                }
            }
            orders[orderKey] = order;
            lastOrderEvent = DateTime.UtcNow;
            if (order.Name == tag)
            {
                entryOrder = order; entryRequested = false;
                entryReportedFilled = Math.Max(entryReportedFilled,filled);
                entryTerminal = entryTerminal || Terminal(orderState);
                entryTerminalFilled = entryTerminalFilled || orderState == OrderState.Filled;
            }
            else if (order.FromEntrySignal == tag && activeId.Length > 0)
                exitReportedByOrder[orderKey] = Math.Max(exitReportedByOrder.ContainsKey(orderKey) ? exitReportedByOrder[orderKey] : 0,filled);
            if (order.Name == "BridgeFlat" && (order.FromEntrySignal == tag || ledgerUncertain) && Terminal(orderState))
            {
                // Filled acknowledgement alone is not a closed-position fill.
                // OnExecutionUpdate clears flattenRequested after quantity zero.
                if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled || error != ErrorCode.NoError)
                { flattenRequested = false; flattenFailed = true; Log("FLATTEN_FAILED",actualQuantity,0,"Native flatten rejected/cancelled; inspect Orders/Positions and flatten manually"); }
            }
            Log("ORDER",quantity,averageFillPrice,order.Name+"|from="+order.FromEntrySignal+"|state="+orderState+"|filled="+filled+"|stop="+Num(stopPrice)+"|limit="+Num(limitPrice)+"|order="+order.OrderId+"|oco="+order.Oco);
            if (orderState == OrderState.Rejected || error != ErrorCode.NoError) Halt("Native order rejected/error: "+error.ToString());
        }
        protected override void OnExecutionUpdate(Execution execution,string executionId,double price,int quantity,MarketPosition marketPosition,string orderId,DateTime time)
        {
            if (execution == null || execution.Order == null || quantity <= 0 || journal == null) return;
            if (String.IsNullOrEmpty(executionId)) { ledgerUncertain = true; Halt("Execution identity missing"); PersistLedger(); return; }
            if (!executionIds.Add(orderId+"|"+executionId)) { Log("DUPLICATE_EXECUTION",quantity,price,"Ignored"); return; }
            OrderAction action = execution.Order.OrderAction;
            bool entry = action == OrderAction.Buy || action == OrderAction.SellShort;
            lastFill = DateTime.UtcNow;
            if (entry)
            {
                if (activeId.Length == 0 || execution.Order.Name != tag || (action == OrderAction.Buy ? 1 : -1) != direction)
                { ledgerUncertain = true; Halt("Unmatched/late entry execution; reconcile account state"); PersistLedger(); return; }
                entryExecuted += quantity;
                if (entryExecuted > submittedQuantity)
                { ledgerUncertain = true; Halt("Entry execution quantity exceeded submitted order; reconcile account state"); PersistLedger(); return; }
                if (actualQuantity == 0) firstEntryFillUtc = Utc(time);
                averageEntry = (averageEntry*actualQuantity+price*quantity)/(actualQuantity+quantity);
                actualQuantity += quantity; realizedNet -= AssumedRoundTurnFee*quantity/2;
                initialRisk = (direction*(averageEntry-stop)*10+AssumedRoundTurnFee+1)*actualQuantity;
                Log("ENTRY_FILL",quantity,price,"exec="+executionId+"|order="+orderId+"|direction="+direction.ToString(CultureInfo.InvariantCulture)+"|fill_utc="+Iso(Utc(time)));
                if (exitExecuted > 0)
                { ledgerUncertain = true; Halt("Late entry fill arrived after an exit; reconcile reopened exposure"); }
                double remainingRisk = (direction*(averageEntry-stop)*10+AssumedRoundTurnFee/2+1)*actualQuantity;
                if (actualQuantity > MaximumContracts || direction*(averageEntry-stop) <= 0 || initialRisk <= 0 || initialRisk > approvedBudget+0.000001 || remainingRisk > 50000+realizedNet-Floor()+0.000001 || direction*(target-averageEntry) <= 0)
                    Halt("Actual entry fill exceeded total risk/remaining account room or target");
                double projectedNet = (direction*(target-averageEntry)*10-AssumedRoundTurnFee/2)*actualQuantity;
                if (!goalReached && 50000+realizedNet-sessionCash+projectedNet > 1200.000001)
                    Halt("Actual entry fill would exceed evaluation profit budget at target");
                if (blocked || flattenRequested) Flatten("Late entry fill during halt");
            }
            else
            {
                if (quantity > actualQuantity || execution.Order.FromEntrySignal != tag || (action == OrderAction.Sell ? 1 : -1) != direction)
                { ledgerUncertain = true; Halt("Exit execution mismatch; manual reconciliation required"); PersistLedger(); return; }
                if (Working(entryOrder)) CancelOrder(entryOrder);
                exitExecuted += quantity;
                realizedNet += (direction*(price-averageEntry)*10-AssumedRoundTurnFee/2)*quantity;
                // A native partial exit reduces the remaining position's
                // original risk proportionally. Do not require the remaining
                // contracts to earn the closed contracts' risk before costBE.
                initialRisk *= (actualQuantity-quantity)/(double)actualQuantity;
                actualQuantity -= quantity;
                Log("EXIT_FILL",quantity,price,"exec="+executionId+"|order="+orderId+"|direction="+direction.ToString(CultureInfo.InvariantCulture)+"|fill_utc="+Iso(Utc(time)));
                if (actualQuantity == 0) { averageEntry = 0; flattenRequested = false; }
            }
            if (50000+realizedNet <= Floor()) Halt("Realized ledger reached account/day reserve");
            PersistLedger();
            Publish();
        }
        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs e)
        {
            if (realtime && (e.PriceStatus != ConnectionStatus.Connected || e.Status != ConnectionStatus.Connected))
                Halt("Connection lost; native cancellation/flatten may await reconnection");
        }
        private void Publish()
        {
            if (runFolder == null || disposing) return;
            DateTime quoteTime = lastBid < lastAsk ? lastBid : lastAsk;
            string[] v = new string[] { "3",runId,Iso(DateTime.UtcNow),realtime ? "REALTIME" : "HISTORICAL",Account.Name,Instrument.FullName,
                blocked ? "1" : "0",reason.Replace(';',' ').Replace('\r',' ').Replace('\n',' '),Iso(lastBar),Num(CurrentBar >= 0 ? Close[0] : 0),Num(GetCurrentBid()),Num(GetCurrentAsk()),Iso(quoteTime),
                actualQuantity.ToString(CultureInfo.InvariantCulture),(actualQuantity == 0 ? 0 : direction).ToString(CultureInfo.InvariantCulture),Num(averageEntry),activeId,Num(stop),Num(target),EntryWorking() ? "1" : "0",Num(realizedNet),Num(sessionCash),Num(highWater),session,
                count.ToString(CultureInfo.InvariantCulture),Num(AssumedRoundTurnFee),"1",Num(initialRisk),OrdersWorking() ? "1" : "0","955",PolicyId,MaximumContracts.ToString(CultureInfo.InvariantCulture),"800","0","1",EnableEvaluationAccount ? "1" : "0","53000",goalReached ? "1" : "0",Iso(actualQuantity == 0 ? DateTime.MinValue : firstEntryFillUtc),entryExecuted.ToString(CultureInfo.InvariantCulture),exitExecuted.ToString(CultureInfo.InvariantCulture),entryReportedFilled.ToString(CultureInfo.InvariantCulture),entryTerminal ? "1" : "0",entryTerminalFilled ? "1" : "0",submittedQuantity.ToString(CultureInfo.InvariantCulture),PendingExitCallbacks().ToString(CultureInfo.InvariantCulture),ledgerUncertain ? "1" : "0",AccountQuantity().ToString(CultureInfo.InvariantCulture) };
            // A reader may briefly hold the Windows destination file. The controller's
            // independent stale-snapshot deadline handles persistent publication failure.
            try { Atomic(Path.Combine(runFolder,"state.txt"),StateHeader+Environment.NewLine+String.Join(";",v)+Environment.NewLine); }
            catch (IOException) { }
        }
        private string Csv(string s) { return "\""+(s ?? "").Replace("\"","\"\"")+"\""; }
        private void Log(string kind,int quantity,double price,string detail)
        {
            if (journal == null) return;
            try { journal.WriteLine(String.Join(",",new string[] { Iso(DateTime.UtcNow),Csv(kind),Csv(activeId),quantity.ToString(CultureInfo.InvariantCulture),Num(price),Num(realizedNet),Csv(detail) })); }
            catch (IOException) { auditFailed = true; }
        }
    }
}

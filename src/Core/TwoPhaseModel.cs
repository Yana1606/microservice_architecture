using Microsoft.Data.Sqlite;
namespace MathTournament.TwoPhaseLab;

// Educational 2PC model with application reservations, not XA or distributed SQLite transactions.
public sealed class Participant
{
    private readonly string file;
    public string Name {get;}
    public Participant(string name,string file)
    {
        Name=name;this.file=file;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var c=Open();using var q=c.CreateCommand();q.CommandText="CREATE TABLE IF NOT EXISTS resource(id INTEGER PRIMARY KEY,balance INTEGER NOT NULL);INSERT OR IGNORE INTO resource VALUES(1,100);CREATE TABLE IF NOT EXISTS votes(id TEXT PRIMARY KEY,amount INTEGER NOT NULL,state TEXT NOT NULL)";q.ExecuteNonQuery();
    }
    private SqliteConnection Open(){var c=new SqliteConnection("Data Source="+file);c.Open();return c;}
    public string State(string id){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT state FROM votes WHERE id=$id";q.Parameters.AddWithValue("$id",id);return (string?)q.ExecuteScalar()??"Unknown";}
    public long Balance{get{using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT balance FROM resource WHERE id=1";return (long)q.ExecuteScalar()!;}}
    public bool Prepare(string id,int amount)
    {
        Require.That(amount>0,"invalid_amount",400);using var c=Open();using var tx=c.BeginTransaction(deferred:false);using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT amount,state FROM votes WHERE id=$id";q.Parameters.AddWithValue("$id",id);
        using(var reader=q.ExecuteReader())if(reader.Read()){Require.That(reader.GetInt64(0)==amount,"transaction_payload_conflict");return reader.GetString(1) is "Prepared" or "Committed";}
        q.CommandText="SELECT balance-COALESCE((SELECT SUM(amount) FROM votes WHERE state='Prepared'),0) FROM resource WHERE id=1";
        var yes=(long)q.ExecuteScalar()!>=amount;q.CommandText="INSERT INTO votes VALUES($id,$amount,$state)";q.Parameters.AddWithValue("$amount",amount);q.Parameters.AddWithValue("$state",yes?"Prepared":"Aborted");q.ExecuteNonQuery();tx.Commit();return yes;
    }
    public void Decide(string id,string decision)
    {
        Require.That(decision is "Commit" or "Abort","invalid_decision");using var c=Open();using var tx=c.BeginTransaction(deferred:false);using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT amount,state FROM votes WHERE id=$id";q.Parameters.AddWithValue("$id",id);long amount;string state;
        using(var reader=q.ExecuteReader()){Require.That(reader.Read(),"transaction_not_prepared");amount=reader.GetInt64(0);state=reader.GetString(1);}
        var target=decision=="Commit"?"Committed":"Aborted";if(state==target)return;
        Require.That(state=="Prepared","decision_conflict");
        if(decision=="Commit"){q.CommandText="UPDATE resource SET balance=balance-$amount WHERE id=1";q.Parameters.AddWithValue("$amount",amount);q.ExecuteNonQuery();}
        q.CommandText="UPDATE votes SET state=$state WHERE id=$id";q.Parameters.AddWithValue("$state",target);q.ExecuteNonQuery();tx.Commit();
    }
    public bool Recover(string id,DecisionChannel channel)
    {
        var decision=channel.Read(id);if(decision is null)return false;Decide(id,decision);return true;
    }
}
public sealed class Coordinator
{
    private readonly string file;
    public Coordinator(string file)
    {
        this.file=file;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);using var c=Open();using var q=c.CreateCommand();q.CommandText="CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY,decision TEXT NOT NULL)";q.ExecuteNonQuery();
    }
    private SqliteConnection Open(){var c=new SqliteConnection("Data Source="+file);c.Open();return c;}
    public string? Read(string id){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT decision FROM decisions WHERE id=$id";q.Parameters.AddWithValue("$id",id);return (string?)q.ExecuteScalar();}
    public string Record(string id,string decision)
    {
        Require.That(decision is "Commit" or "Abort","invalid_decision");using var c=Open();using var tx=c.BeginTransaction(deferred:false);using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="INSERT OR IGNORE INTO decisions VALUES($id,$decision)";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$decision",decision);q.ExecuteNonQuery();
        q.CommandText="SELECT decision FROM decisions WHERE id=$id";var saved=(string)q.ExecuteScalar()!;Require.That(saved==decision,"decision_conflict");tx.Commit();return saved;
    }
    public string Run(string id,Participant first,Participant second,int firstAmount,int secondAmount,bool stopAfterDecision=false)
    {
        var saved=Read(id);
        if(saved is null){var firstYes=first.Prepare(id,firstAmount);var secondYes=second.Prepare(id,secondAmount);saved=Record(id,firstYes&&secondYes?"Commit":"Abort");}
        if(!stopAfterDecision){first.Decide(id,saved);second.Decide(id,saved);}return saved;
    }
}
// Models the coordinator's decision endpoint. It never reads participant databases.
public sealed class DecisionChannel(Coordinator coordinator)
{
    public bool Available{get;set;}=true;
    public string? Read(string id)=>Available?coordinator.Read(id):throw new HttpRequestException("Coordinator unavailable; remain Prepared");
}
public static class Example
{
    public static string Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var log=new List<string>{"Educational 2PC: application reservations in three independent SQLite databases; not XA."};
        void Scenario(string name,int amount,bool crash)
        {
            var root=Path.Combine(folder,name);var a=new Participant("A",Path.Combine(root,"a.db"));var b=new Participant("B",Path.Combine(root,"b.db"));var c=new Coordinator(Path.Combine(root,"coordinator.db"));
            log.Add(name+": Prepare A/B");
            if(crash)
            {
                a.Prepare("tx",10);b.Prepare("tx",10);var channel=new DecisionChannel(c){Available=false};
                try{a.Recover("tx",channel);}catch(HttpRequestException){log.Add("coordinator unavailable: A remains "+a.State("tx")+", balance="+a.Balance);}
                c=new Coordinator(Path.Combine(root,"coordinator.db"));c.Record("tx","Commit");channel=new DecisionChannel(c);a.Recover("tx",channel);b.Recover("tx",channel);
            }
            else c.Run("tx",a,b,10,amount);
            log.Add(name+": decision="+c.Read("tx")+", A="+a.State("tx")+", B="+b.State("tx")+", balances="+a.Balance+"/"+b.Balance);
        }
        Scenario("commit",10,false);Scenario("abort",110,false);Scenario("recovery",10,true);return string.Join(Environment.NewLine,log);
    }
}

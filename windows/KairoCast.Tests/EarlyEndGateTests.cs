using KairoCast.App.Shell;

namespace KairoCast.Tests;

public class EarlyEndGateTests
{
    private sealed class Sess { }

    [Fact]
    public void Partager_arreter_repartager_ne_ferme_pas_la_seconde_session()
    {
        var gate = new EarlyEndGate<Sess>();
        var ended = new List<Sess>();

        // Premier partage : démarre, puis fin normale.
        gate.Reset();
        var s1 = new Sess();
        Assert.False(gate.Started(s1, out _));
        gate.Ended(null, (s, e) => ended.Add(s));
        Assert.Equal(new[] { s1 }, ended);

        // Second partage : aucune fin précoce héritée.
        gate.Reset();
        var s2 = new Sess();
        Assert.False(gate.Started(s2, out var early));
        Assert.Null(early);
        Assert.Same(s2, gate.Session);
    }

    [Fact]
    public void Fin_avant_le_retour_du_demarrage_est_signalee_une_fois()
    {
        var gate = new EarlyEndGate<Sess>();
        gate.Reset();
        var boom = new InvalidOperationException("x");
        var called = false;
        gate.Ended(boom, (s, e) => called = true);
        Assert.False(called);
        Assert.True(gate.Started(new Sess(), out var e));
        Assert.Same(boom, e);

        // Une fois consommée, elle ne se répète pas.
        Assert.False(gate.Started(new Sess(), out _));
    }

    [Fact]
    public void Reset_efface_une_fin_precoce_restante()
    {
        var gate = new EarlyEndGate<Sess>();
        gate.Ended(null, (s, e) => { });
        gate.Reset();
        Assert.False(gate.Started(new Sess(), out _));
    }
}

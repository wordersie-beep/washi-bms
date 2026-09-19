using cAlgo.API;
namespace Quant.Bot { [Robot(AccessRights = AccessRights.None)] public class Probe : Robot {
  protected override void OnStart() { Print("{0}", Symbol.Bid); }
  protected override double GetFitness(GetFitnessArgs args) { return args.NetProfit; } } }

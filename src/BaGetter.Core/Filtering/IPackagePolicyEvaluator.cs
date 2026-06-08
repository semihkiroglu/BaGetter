namespace BaGetter.Core;

public interface IPackagePolicyEvaluator
{
    PackageFilterDecision Evaluate(PackageFilterContext context);
}

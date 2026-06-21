using System;

namespace Fx.ControlKit.Llm;

public class CosineAnnealingWithWarmup
{
    public double BaseLr { get; }
    public double MinLr { get; }
    public int WarmupSteps { get; }
    public int MaxSteps { get; }

    public CosineAnnealingWithWarmup(double baseLr, double minLr, int warmupSteps, int maxSteps)
    {
        BaseLr = baseLr;
        MinLr = minLr;
        WarmupSteps = warmupSteps;
        MaxSteps = maxSteps;
    }

    public double GetLearningRate(int currentStep)
    {
        if (currentStep < WarmupSteps)
        {
            return BaseLr * ((double)currentStep / Math.Max(1, WarmupSteps));
        }

        if (currentStep >= MaxSteps)
        {
            return MinLr;
        }

        double progress = (double)(currentStep - WarmupSteps) / Math.Max(1, MaxSteps - WarmupSteps);
        return MinLr + 0.5 * (BaseLr - MinLr) * (1.0 + Math.Cos(progress * Math.PI));
    }
}

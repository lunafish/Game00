using System.Collections.Generic;
using UnityEngine;
using System.Text;

namespace CityGen
{
    [System.Serializable]
    public struct Rule
    {
        public char input;
        public string outputs; // Separated by comma if multiple? Or just single string for now.
                               // Let's stick to simple deterministic or single stochastic for now.
                               // Actually, let's keep it simple: Single string output.
    }

    public class LSystem
    {
        public static string GenerateSentence(string axiom, Rule[] rules, int iterations)
        {
            string current = axiom;
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < iterations; i++)
            {
                sb.Clear();
                foreach (char c in current)
                {
                    bool found = false;
                    foreach (var rule in rules)
                    {
                        if (rule.input == c)
                        {
                            sb.Append(rule.outputs);
                            found = true;
                            break;
                        }
                    }
                    if (!found) sb.Append(c);
                }
                current = sb.ToString();
            }
            return current;
        }
    }
}

using UnityEngine;
using System;

[Serializable]
public class DiseaseProfile
{
    public string diseaseName;

    public DiseaseProfile(string name) { diseaseName = name; }

    public static readonly DiseaseProfile[] Presets = new[]
    {
        new DiseaseProfile("Covid-19"),
        new DiseaseProfile("Influenza"),
        new DiseaseProfile("Common cold")

    };
}

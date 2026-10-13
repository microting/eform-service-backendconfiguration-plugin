namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals.Db;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChemicalsBase.Infrastructure;
using ChemicalsBase.Infrastructure.Data.Entities;

public static class ChemicalJobSeed
{
    public static async Task<int> AddChemicalAsync(
        ChemicalsDbContext db, string name, int status = 5, DateTime? salesDeadline = null, DateTime? useAndPossessionDeadline = null)
    {
        var chemical = new Chemical
        {
            Name = name,
            RegistrationNo = Random.Shared.Next(1, 999) + "-" + Random.Shared.Next(1, 999),
            RemoteId = Guid.NewGuid().ToString("N"),
            Status = status,
            SalesDeadline = salesDeadline,
            UseAndPossesionDeadline = useAndPossessionDeadline,
            AuthorisationHolder = new AuthorisationHolder
            {
                RemoteId = Guid.NewGuid().ToString("N"), Name = "Holder A/S", Address = new Address { Country = 45 },
            },
            ClassificationAndLabeling = new ClassificationAndLabeling
            {
                CLP = new CLP { HazardPictograms = new List<int> { 7 }, SignalWord = 1, HazardStatements = new List<HazardStatement>() },
                DPD = new DPD { RiskPhrases = new List<int>() },
            },
            BiocideUser = new List<int>(),
            PesticideProductGroup = new List<int>(),
            BiocideProductType = new List<int>(),
            PesticidePossibleUse = new List<int>(),
            BiocidePossibleUse = new List<int>(),
            BiocideSpecialUse = new List<int>(),
            BarcodeValue = new List<string>(),
            ActiveSubstances = new List<ActiveSubstance>(),
            Products = new List<Product>(),
        };
        db.Chemicals.Add(chemical);
        await db.SaveChangesAsync();
        return chemical.Id;
    }
}

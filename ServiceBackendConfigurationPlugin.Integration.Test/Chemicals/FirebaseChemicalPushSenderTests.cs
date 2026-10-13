namespace ServiceBackendConfigurationPlugin.Integration.Test.Chemicals;

using FirebaseAdmin.Messaging;
using NUnit.Framework;
using ServiceBackendConfigurationPlugin.Infrastructure.Chemicals;

[TestFixture]
public class FirebaseChemicalPushSenderTests
{
    [TestCase(MessagingErrorCode.Unregistered, ChemicalPushOutcome.DeadToken)]
    [TestCase(MessagingErrorCode.InvalidArgument, ChemicalPushOutcome.InvalidArgument)]
    [TestCase(MessagingErrorCode.SenderIdMismatch, ChemicalPushOutcome.SenderIdMismatch)]
    [TestCase(MessagingErrorCode.Unavailable, ChemicalPushOutcome.Transient)]
    [TestCase(MessagingErrorCode.Internal, ChemicalPushOutcome.Transient)]
    [TestCase(MessagingErrorCode.QuotaExceeded, ChemicalPushOutcome.Transient)]
    [TestCase(null, ChemicalPushOutcome.Transient)]
    public void Classify_MapsFcmErrorsLikeTheAdhocSender(MessagingErrorCode? code, ChemicalPushOutcome expected)
    {
        Assert.That(FirebaseChemicalPushSender.Classify(code), Is.EqualTo(expected));
    }
}

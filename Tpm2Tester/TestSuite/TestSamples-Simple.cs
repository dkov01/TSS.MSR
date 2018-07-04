﻿/* 
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See the LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Tpm2Lib;
using Tpm2Tester;

//
// This file contains examples of TPM 2.0 test routines
//
// Note that the names of the namespace and class(es) containing the tests can be any.
//

namespace Tpm2TestSuite
{
    partial class Tpm2Tests
    {
        // A test case method must be marked with 
        [Test(Profile.TPM20, Privileges.StandardUser, Category.Misc, Special.None)]
        void TestRandom(Tpm2 tpm, TestContext testCtx)
        {
            // Check that the TPM returns the correct number of random bytes for various lengths
            testCtx.ReportParams("Test phase: GetRandom");
            for (int j = 0; j < TestConfig.NumIters; j++)
            {
                int numBytes = Substrate.RandomInt(TpmCfg.MaxDigestSize);
                byte[] rx = tpm.GetRandom((ushort)numBytes);
                testCtx.AssertEqual("CorrectNumBytes", rx.Length, numBytes);
            }

            // Check that the TPM can accept stir data up to test-defined maximum
            testCtx.ReportParams("Test phase: StirRandom");
            for (int j = 0; j < TestConfig.NumIters; j++)
            {
                byte[] toStir = Substrate.RandomBytes(Substrate.RandomInt(TpmCfg.MaxDigestSize));
                tpm.StirRandom(toStir);
                int numBytes = Substrate.RandomInt(TpmCfg.MaxDigestSize);
                byte[] rx = tpm.GetRandom((ushort)numBytes);
                testCtx.AssertEqual("CorrectNumBytes.AfterStir", rx.Length, numBytes);
            }
        } // TestRandom

        [Test(Profile.TPM20, Privileges.Admin, Category.Misc, Special.None)]
        void TestVendorSpecific(Tpm2 tpm, TestContext testCtx)
        {
            if (!TpmCfg.IsImplemented(TpmCc.VendorTcgTest))
            {
                Substrate.WriteToLog("TestVendorSpecific skipped", ConsoleColor.DarkCyan);
                return;
            }

            TpmHandle h = Substrate.CreateDataObject(tpm);
            byte[] inData = Substrate.RandomBytes(24);

            testCtx.ReportParams("Input data size: " + inData.Length);
            byte[] outData = tpm.VendorTcgTest(inData);
            testCtx.Assert("CertDataReceived", outData.Length > 0, outData.Length);
           
            tpm.FlushContext(h);
        } // TestVendorSpecific

        [Test(Profile.TPM20, Privileges.Admin, Category.Asym | Category.Object)]
        void ActivateAikSample(Tpm2 tpm, TestContext testCtx)
        {
            // Use an RSA primary key in the Endorsement hierarchy (EK) to 'activate'
            // another primary in the Endorsement hierarchy (AIK).

            // Note: this procedure can be used to activate a key in the Storage hierarchy, too.

            // "Activation" means secure passing sensitive data generated by a CA (e.g.
            // a symmetric key protecting a freshly generated certificate), so that only
            // a device with the TPM owning the target AIK and EK can access these data.

            // Create a primary key that we will certify.

            //////////////////////////////////////////////////////////////////////////////
            // Device side code
            //////////////////////////////////////////////////////////////////////////////

            // Name algorithm of the new AIK
            TpmAlgId nameAlg = TpmAlgId.Sha256;

            // The key to be certified needs a policy. It can be ANY policy, but here
            // it is configured to allow AIK usage only with the following commands
            var policyAIK = new PolicyTree(nameAlg);
            var policyOR = new TpmPolicyOr();
            policyAIK.SetPolicyRoot(policyOR);
            policyOR.AddPolicyBranch(new TpmPolicyCommand(TpmCc.ActivateCredential, "Activate"));
            policyOR.AddPolicyBranch(new TpmPolicyCommand(TpmCc.Certify, "Certify"));
            policyOR.AddPolicyBranch(new TpmPolicyCommand(TpmCc.CertifyCreation, "CertifyCreation"));
            policyOR.AddPolicyBranch(new TpmPolicyCommand(TpmCc.Quote, "Quote"));

            var inAIKPub = new TpmPublic(nameAlg,
                ObjectAttr.Restricted | ObjectAttr.Sign | ObjectAttr.FixedParent | ObjectAttr.FixedTPM
                    | ObjectAttr.AdminWithPolicy | ObjectAttr.SensitiveDataOrigin,
                policyAIK.GetPolicyDigest(),
                new RsaParms(new SymDefObject(), new SchemeRsassa(TpmAlgId.Sha256), 2048, 0),
                new Tpm2bPublicKeyRsa());

            TpmPublic AIKpub;
            var inSens = new SensitiveCreate(Substrate.RandomAuth(nameAlg), null);
            CreationData creationData;
            byte[] creationHash;
            TkCreation creationTk;
            TpmHandle hAIK = tpm.CreatePrimary(TpmRh.Endorsement, inSens, inAIKPub, null, null, out AIKpub,
                                               out creationData, out creationHash, out creationTk);
            // An alternative using test substrate helper
            //TpmHandle hAIK = Substrate.CreatePrimary(tpm, inAIKPub, TpmRh.Endorsement);

            // Normally a device would have a pre-provisioned persistent EK with the above handle
            TpmHandle hEK = new TpmHandle(0x81010001);

            // Get its public part
            TpmPublic EKpub = null;

            // In a test environment the real EK may be absent. In this case use
            // a primary key temporarily created in the Endorsement hierarchy.
            byte[] name, qname;
            tpm._AllowErrors().ReadPublic(hEK, out name, out qname);
            if (!tpm._LastCommandSucceeded())
            {
                var inEKpub = new TpmPublic(TpmAlgId.Sha256,
                        ObjectAttr.Restricted | ObjectAttr.Decrypt | ObjectAttr.FixedParent | ObjectAttr.FixedTPM
                            | ObjectAttr.AdminWithPolicy | ObjectAttr.SensitiveDataOrigin,
                        new byte[] { 0x83, 0x71, 0x97, 0x67, 0x44, 0x84, 0xb3, 0xf8,
                                     0x1a, 0x90, 0xcc, 0x8d, 0x46, 0xa5, 0xd7, 0x24,
                                     0xfd, 0x52, 0xd7, 0x6e, 0x06, 0x52, 0x0b, 0x64,
                                     0xf2, 0xa1, 0xda, 0x1b, 0x33, 0x14, 0x69, 0xaa },
                        new RsaParms(new SymDefObject(TpmAlgId.Aes, 128, TpmAlgId.Cfb), null, 2048, 0),
                        new Tpm2bPublicKeyRsa());

                inSens = new SensitiveCreate(null, null);
                hEK = tpm.CreatePrimary(TpmRh.Endorsement, inSens, inEKpub, null, null, out EKpub,
                                        out creationData, out creationHash, out creationTk);
            }

            // Marshal public parts of AIK and EK

            byte[] AIKpubBytes = AIKpub.GetTpmRepresentation();
            byte[] EKpubBytes = EKpub.GetTpmRepresentation();

            // Here the device uses network to pass AIKpubBytes and EKpubBytes (as well as
            // the EK certificate) to CA service.


            //////////////////////////////////////////////////////////////////////////////
            // Service (CA) side code
            //////////////////////////////////////////////////////////////////////////////

            // Unmarshal AIKpub and EKpub (and EK certificate)

            var m = new Marshaller(AIKpubBytes);
            AIKpub = m.Get<TpmPublic>();
            m = new Marshaller(EKpubBytes);
            EKpub = m.Get<TpmPublic>();

            // Symmetric key to be used by CA to encrypt the new certificate it creates for AIK
            byte[] secretKey = Substrate.RandomBytes(32);

            // Here CA does the following (the sample does not show this code):
            // - Validates EK certificate
            // - Generates a new certificate for AIK (AIKcert)
            // - Encrypts AIKcert with secretKey

            // Create an activation blob

            // Internal wrapper key encrypted with EKpub to be used by TPM2_ActivateCredential() command.
            byte[] encSecret;

            // An encrypted and HMACed object tied to the destination TPM. It contains
            // 'secret' to be extracted by the successful TPM2_ActivateCredential() command
            // (that can only succeeed on the TPM that originated both EK and AIK).
            IdObject certInfo;

            // Run TSS.Net equivalent of TPM2_MakeCredential() command (convenient for the server side,
            // as it will likely be faster than the TPM transaction and allows concurrent execution).
            certInfo = EKpub.CreateActivationCredentials(secretKey, AIKpub.GetName(), out encSecret);

            // Marshal certInfo

            // IdObject data type requires customized marshaling
            m = new Marshaller();
            m.Put(certInfo.integrityHMAC.Length, "integrityHMAC.Length");
            m.Put(certInfo.integrityHMAC, "integrityHMAC");
            m.Put(certInfo.encIdentity.Length, "encIdentity");
            m.Put(certInfo.encIdentity, "encIdentity.Length");
            byte[] certInfoBytes = m.GetBytes();

            // Here the CA passes certInfoBytes and encSecret (as well as the encrypted
            // AIK certificate) back to the device via network.


            //////////////////////////////////////////////////////////////////////////////
            // Device side code again
            //////////////////////////////////////////////////////////////////////////////

            // Unmarshal certInfo and encSecret (and encrypted AIK certificate)

            m = new Marshaller(certInfoBytes);
            int len = m.Get<int>();
            certInfo.integrityHMAC = m.GetArray<byte>(len);
            len = m.Get<int>();
            certInfo.encIdentity = m.GetArray<byte>(len);
            // encSecret is a byte array, so, normally, no special unmarshalling is required

            // Create policy session to authorize AIK usage
            AuthSession sessAIK = tpm.StartAuthSessionEx(TpmSe.Policy, nameAlg);
            sessAIK.RunPolicy(tpm, policyAIK, "Activate");

            // Create policy description and corresponding policy session to authorize EK usage
            var policyEK = new PolicyTree(EKpub.nameAlg);
            policyEK.SetPolicyRoot(new TpmPolicySecret(TpmRh.Endorsement, false, 0, null, null));

            AuthSession sessEK = tpm.StartAuthSessionEx(TpmSe.Policy, EKpub.nameAlg);
            sessEK.RunPolicy(tpm, policyEK);

            byte[] recoveredSecretKey = tpm[sessAIK, sessEK].ActivateCredential(hAIK, hEK, certInfo, encSecret);

            testCtx.AssertEqual("Secret.1", recoveredSecretKey, secretKey);

            // Here the device can use recoveredSecretKey to decrypt the AIK certificate

            //////////////////////////////////////////////////////////////////////////////
            // End of activation sequence
            //////////////////////////////////////////////////////////////////////////////


            //
            // Now prepare activation using the TPM built-in command
            //

            byte[] encSecret2 = null;
            IdObject certInfo2 = tpm.MakeCredential(hEK, secretKey, AIKpub.GetName(), out encSecret2);

            // Reinitialize policy sessions
            tpm.PolicyRestart(sessAIK);
            sessAIK.RunPolicy(tpm, policyAIK, "Activate");
            tpm.PolicyRestart(sessEK);
            sessEK.RunPolicy(tpm, policyEK);

            recoveredSecretKey = tpm[sessAIK, sessEK].ActivateCredential(hAIK, hEK, certInfo2, encSecret2);
            testCtx.AssertEqual("Secret.2", recoveredSecretKey, secretKey);

            // Cleanup
            tpm.FlushContext(sessAIK);
            tpm.FlushContext(sessEK);
            tpm.FlushContext(hAIK);

            if (hEK.handle != 0x81010001)
                tpm.FlushContext(hEK);
        } // ActivateAikSample

    }
}


﻿#define PARALLEL
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using BSAsharp;
using System.IO.MemoryMappedFiles;
using TaleOfTwoWastelands.ProgressTypes;
using System.Reactive.Subjects;

namespace TaleOfTwoWastelands.Patching
{
    public class BSADiff
    {
        public const string VOICE_PREFIX = @"sound\voice";
        public static readonly string PatchDir = Path.Combine(Installer.AssetsDir, "TTW Data", "TTW Patches");

        protected IProgress<string> ProgressLog { get; set; }
        protected IProgress<OperationProgressUpdate> ProgressMinorUI { get; set; }
        protected CancellationToken Token { get; set; }
        protected InstallOperation Op { get; set; }

        private void Log(string msg)
        {
            ProgressLog.Report('\t' + msg);
        }

        public BSADiff(IProgress<string> ProgressLog, IProgress<OperationProgressUpdate> ProgressMinorUI, CancellationToken Token)
        {
            this.ProgressLog = ProgressLog;
            this.ProgressMinorUI = ProgressMinorUI;
            this.Token = Token;
        }

        public bool PatchBSA(CompressionOptions bsaOptions, string oldBSA, string newBSA, bool simulate = false)
        {
            Op = new InstallOperation(ProgressMinorUI, Token) { ItemsTotal = 7 };

            var outBsaFilename = Path.GetFileNameWithoutExtension(newBSA);

            BSAWrapper BSA;
            try
            {
                Op.CurrentOperation = "Opening " + Path.GetFileName(oldBSA);

                BSA = new BSAWrapper(oldBSA, bsaOptions);
            }
            finally
            {
                Op.Step();
            }

            var renameDict = new Dictionary<string, string>();
            try
            {
                Op.CurrentOperation = "Opening rename database";

                var renamePath = Path.Combine(PatchDir, Path.ChangeExtension(outBsaFilename, ".ren"));
                if (File.Exists(renamePath))
                    using (var stream = File.OpenRead(renamePath))
                    using (var reader = new BinaryReader(stream))
                        while (stream.Position < stream.Length)
                            renameDict.Add(reader.ReadString(), reader.ReadString());
            }
            finally
            {
                Op.Step();
            }

            IDictionary<string, PatchInfo[]> patchDict;
            try
            {
                Op.CurrentOperation = "Opening patch database";

                var patchPath = Path.Combine(PatchDir, Path.ChangeExtension(outBsaFilename, ".pat"));
                if (File.Exists(patchPath))
                {
                    patchDict = new PatchDict(patchPath);
                }
                else
                {
                    Log("\tNo patch database is available for: " + oldBSA);
                    return false;
                }
            }
            finally
            {
                Op.Step();
            }

            using (BSA)
            {
                try
                {
                    RenameFiles(BSA, renameDict);

                    if (renameDict.Count > 0)
                    {
                        foreach (var kvp in renameDict)
                        {
                            Log("File not found: " + kvp.Value);
                            Log("\tCannot create: " + kvp.Key);
                        }
                    }
                }
                finally
                {
                    Op.Step();
                }

                var allFiles = BSA.SelectMany(folder => folder).ToList();
                try
                {
                    var opChk = new InstallOperation(ProgressMinorUI, Token);

                    var oldChkDict = FileValidation.FromBSA(BSA);
                    opChk.ItemsTotal = patchDict.Count;

                    var joinedPatches = from patKvp in patchDict
                                        join oldKvp in oldChkDict on patKvp.Key equals oldKvp.Key into foundOld
                                        join bsaFile in allFiles on patKvp.Key equals bsaFile.Filename
                                        select new
                                        {
                                            bsaFile,
                                            file = patKvp.Key,
                                            patches = patKvp.Value,
                                            oldChk = foundOld.SingleOrDefault()
                                        };

#if PARALLEL
                    Parallel.ForEach(joinedPatches, join =>
#else
                    foreach (var join in joinedPatches)
#endif
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(join.oldChk.Key))
                            {
                                //file not found
                                Log("File not found: " + join.file);

#if PARALLEL
                                return;
#else
                                continue;
#endif
                            }

                            foreach (var patchInfo in join.patches)
                            {
                                var newChk = patchInfo.Metadata;
                                if (FileValidation.IsEmpty(newChk) && patchInfo.Data.Length == 0)
                                {
                                    if (join.bsaFile.Filename.StartsWith(VOICE_PREFIX))
                                    {
                                        Log("Skipping voice file " + join.bsaFile.Filename);
#if PARALLEL
                                        return;
#else
                                    continue;
#endif
                                    }
                                    else
                                    {
                                        Log("Empty patch for file " + join.bsaFile.Filename);
#if PARALLEL
                                        return;
#else
                                    continue;
#endif
                                    }
                                }

                                var lazyOldChk = join.oldChk.Value;
                                using (var oldChk = lazyOldChk.Value)
                                {
                                    opChk.CurrentOperation = "Validating " + join.bsaFile.Name;

                                    if (!newChk.Equals(oldChk))
                                    {
                                        opChk.CurrentOperation = "Patching " + join.bsaFile.Name;

                                        if (!PatchFile(join.bsaFile, oldChk, patchInfo))
                                            Log(string.Format("Patching {0} failed", join.bsaFile.Filename));
                                    }
                                }
                            }
                        }
                        finally
                        {
                            opChk.Step();
                        }
                    }
#if PARALLEL
);
#endif
                }
                finally
                {
                    Op.Step();
                }

                try
                {
                    Op.CurrentOperation = "Removing unnecessary files";

                    var notIncluded = allFiles.Where(file => !patchDict.ContainsKey(file.Filename));
                    var filesToRemove = new HashSet<BSAFile>(notIncluded);

                    var filesRemoved = BSA.Sum(folder => folder.RemoveWhere(bsafile => filesToRemove.Contains(bsafile)));
                    BSA.RemoveWhere(folder => folder.Count == 0);
                }
                finally
                {
                    Op.Step();
                }

                try
                {
                    Op.CurrentOperation = "Building " + Path.GetFileName(newBSA);

                    if (!simulate)
                        BSA.Save(newBSA.ToLowerInvariant());
                }
                finally
                {
                    Op.Step();
                }
            }

            Op.Finish();

            return true;
        }

        public void RenameFiles(BSAWrapper BSA, Dictionary<string, string> renameDict)
        {
            var opPrefix = "Renaming BSA files";

            var opRename = new InstallOperation(ProgressMinorUI, Token);
            opRename.CurrentOperation = opPrefix;

            var renameGroup = from folder in BSA
                              from file in folder
                              join kvp in renameDict on file.Filename equals kvp.Value
                              let a = new { folder, file, kvp }
                              //group a by kvp.Value into g
                              select a;

            var renameCopies = from g in renameGroup
                               let newFilename = g.kvp.Key
                               let newDirectory = Path.GetDirectoryName(newFilename)
                               let a = new { g.folder, g.file, newFilename }
                               group a by newDirectory into outs
                               select outs;

            var newBsaFolders = from g in renameCopies
                                let folderAdded = BSA.Add(new BSAFolder(g.Key))
                                select g;
            newBsaFolders.ToList();

            opRename.ItemsTotal = BSA.SelectMany(folder => folder).Count();

            var renameFixes = from g in newBsaFolders
                              from a in g
                              join newFolder in BSA on g.Key equals newFolder.Path
                              let newFile = a.file.DeepCopy(g.Key, Path.GetFileName(a.newFilename))
                              let addedFile = newFolder.Add(newFile)
                              //let removedFile = a.folder.Remove(a.file)
                              //don't say this too fast
                              let cleanedDict = renameDict.Remove(a.newFilename)

                              let curOp = (opRename.CurrentOperation = opPrefix + ": " + a.file.Name + " -> " + newFile.Name)
                              let curDone = opRename.Step()

                              select new { a.folder, a.file, newFolder, newFile, a.newFilename };
            renameFixes
#if PARALLEL
.AsParallel()
#endif
.ToList(); // execute query
        }

        public bool PatchFile(BSAFile bsaFile, FileValidation oldChk, PatchInfo patch, bool failFast = false)
        {
            bool perfect = true;

            //file exists but is not up to date
            if (patch.Data != null)
            {
                //a patch exists for the file

                //InflaterInputStream won't let the patcher seek it,
                //so we have to perform a new allocate-and-copy
                var inputBytes = bsaFile.GetContents(true);

                using (MemoryStream
                    output = new MemoryStream())
                {
                    unsafe
                    {
                        fixed (byte* pInput = inputBytes)
                        fixed (byte* pPatch = patch.Data)
                            BinaryPatchUtility.Apply(pInput, inputBytes.Length, pPatch, patch.Data.Length, output);
                    }

                    output.Seek(0, SeekOrigin.Begin);
                    using (var testChk = new FileValidation(output))
                    {
                        if (patch.Metadata.Equals(testChk))
                            bsaFile.UpdateData(output.ToArray(), false);
                        else
                        {
                            var err = "Patching " + bsaFile.Filename + " has failed - " + testChk;
                            if (failFast)
                                Trace.Fail(err);
                            else
                            {
                                perfect = false;
                                Log(err);
                            }
                        }
                    }
                }
            }
            else
            {
                //no patch exists for the file
                var err = "File is of an unexpected version: " + bsaFile.Filename + " - " + oldChk;

                if (failFast)
                    Trace.Fail(err);
                else
                {
                    perfect = false;
                    Log(err);
                    Log("\tThis file cannot be patched. Errors may occur.");
                }
            }

            return perfect;
        }
    }
}

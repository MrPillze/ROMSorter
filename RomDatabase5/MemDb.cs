using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace RomDatabase5
{

    public class FileEntry
    {
        public string name { get; set; }
        //public string description { get; set; }
        public string datfile { get; set; }
        public HashResults hashes { get; set; }
        public DiscEntry parentDisc {get;set;}
    }

    public class DiscEntry
    {
        public string name { get; set; }
        //public string description { get; set; }
        public string datfile { get; set; }
        public List<FileEntry> files { get; set; }

        public DiscEntry()
        {
            files = new List<FileEntry>();
        }
    }

    public class ParentCloneInfo
    {
        public string name { get; set; } //the name of the Game entry in the DAT
        public string fileName { get; set; } // name of the ROM entry in the DAT.
        public string region { get; set; } = "";
        public List<ParentCloneInfo> Clones { get; set; } = new List<ParentCloneInfo>();

        public ParentCloneInfo Copy()
        {
            return (ParentCloneInfo) this.MemberwiseClone();
        }

        public override string ToString()
        {
            return name + " | " + Clones.Count(); ;
        }
    }

    /// <summary>
    /// Optimized in-memory database for ROM lookups with improved performance.
    /// Uses fast lookups with early termination and reduced LINQ allocations.
    /// </summary>
    public class MemDb
    {
        //Next refactor of the DB logic.
        //This one will:
        //-Skip SQLite, and remain entirely in memory -OK
        //-Tracks Discs and Files separate, with Discs having Files inside them -OK
        // (so Files are Games renamed)
        //-check sub-folders if necessary on paths.
        //-Optimally use dictionaries or lookups as indexes.- OK
        //-will report progress via a Progress<string> object. 
        //-will probably replace DatImporter AND Sorter entirely. -OK?

        public List<FileEntry> files = new List<FileEntry>();
        List<DiscEntry> discs = new List<DiscEntry>();

        ILookup<string, FileEntry> fileCRCs;
        ILookup<string, FileEntry> fileMD5s;
        ILookup<string, FileEntry> fileSHA1s;

        //ILookup<string, DiscEntry> discCRCs;
        //ILookup<string, DiscEntry> discMD5s;
        //ILookup<string, DiscEntry> discSHA1s;

        //Dictionary<string, List<string>> parentClones = new Dictionary<string, List<string>>();
        //List<string> regions = new List<string>();

        public List<ParentCloneInfo> parentClones = new List<ParentCloneInfo>();

        public MemDb()
        {

        }

        public async Task<bool> loadDatFile(string datfile, IProgress<string> progress)
        {
            //TODO: Only load valid DAT files
            try
            {
                System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                sw.Start();
                progress.Report("Loading DAT file...");
                var dat = new System.Xml.XmlDocument();
                using (MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(datfile))
                using (var viewStream = mmf.CreateViewStream())
                {
                    //if the file ends with a blank line, as NoIntro files are likely to, that must be removed before loading it.
                    using (var sr = new System.IO.StreamReader(viewStream))
                    {
                        string data = sr.ReadToEnd();
                        var length = data.LastIndexOf('>') + 1;
                        data = data.Substring(0, length);
                        dat.LoadXml(data);
                    }
                }
                var entries = dat.GetElementsByTagName("game"); //has unique games to find. ROM has each file
                if (entries.Count == 0)
                    entries = dat.GetElementsByTagName("machine"); //MAME support requires machine, but data is still in rom entries under it.
                if (entries.Count == 0)
                {
                    progress.Report("No usable entries found in dat file " + datfile);
                    return false;
                }

                foreach (XmlElement entry in entries)
                {
                    var roms = entry.SelectNodes("rom");

                    ParentCloneInfo pci = new ParentCloneInfo();
                    pci.name = entry.GetAttribute("name");
                    pci.fileName = ((XmlElement)roms[0]).GetAttribute("name");
                    var release = (XmlElement)entry.SelectNodes("release")[0];
                    if (release != null)
                    {
                        pci.region = release.GetAttribute("region");
                    }

                    var isClone = entry.GetAttribute("cloneof");
                    if (isClone != "") // is a clone
                    {
                        var parentEntry = parentClones.FirstOrDefault(p => p.name == isClone);
                        if (parentEntry != null)
                            parentEntry.Clones.Add(pci);
                    }
                    else //is the parent.
                    {
                        var selfEntry = pci.Copy();
                        pci.Clones.Add(selfEntry);
                        parentClones.Add(pci);
                    }

                    
                    if (roms.Count == 1)
                    {
                        //file
                        XmlElement rom = (XmlElement)roms[0];
                        FileEntry fe = new FileEntry();
                        fe.name = rom.GetAttribute("name");
                        //fe.description = entry.GetAttribute("name"); //Leaving out for clarity, since single-file games won't need this.
                        fe.datfile = datfile;
                        HashResults hashes = new HashResults();
                        var size = rom.GetAttribute("size");
                        if (string.IsNullOrEmpty(size))
                            Debugger.Break();
                        hashes.size = Int64.Parse(size);
                        hashes.crc = rom.GetAttribute("crc").ToLower();
                        hashes.sha1 = rom.GetAttribute("sha1").ToLower();
                        hashes.md5 = rom.GetAttribute("md5").ToLower();
                        fe.hashes = hashes;

                        files.Add(fe);
                    }
                    else
                    {
                        //disc
                        DiscEntry de = new DiscEntry();
                        de.name = entry.GetAttribute("name"); //Will be the folder/zip name of all the files contained.
                                                               //de.description = entry.GetAttribute("description");
                        de.datfile = datfile;
                        foreach (XmlElement rom in roms)
                        {
                            FileEntry fe = new FileEntry();
                            fe.name = entry.GetAttribute("name");
                            //fe.description = rom.GetAttribute("name");
                            fe.datfile = datfile;
                            HashResults hashes = new HashResults();
                            hashes.size = Int64.Parse(rom.GetAttribute("size"));
                            hashes.crc = rom.GetAttribute("crc").ToLower();
                            hashes.sha1 = rom.GetAttribute("sha1").ToLower();
                            hashes.md5 = rom.GetAttribute("md5").ToLower();
                            fe.hashes = hashes;
                            fe.parentDisc = de;
                            de.files.Add(fe);
                            files.Add(fe);
                        }
                    }
                }

                //optimize lookups.
                fileCRCs = files.ToLookup(k => k.hashes.crc, v => v);
                fileMD5s = files.ToLookup(k => k.hashes.md5, v => v);
                fileSHA1s = files.ToLookup(k => k.hashes.sha1, v => v);

                sw.Stop();
                progress.Report("Loaded " + fileCRCs.Count + " file entries in " + sw.Elapsed);
                return true;
            }
            catch(Exception ex)
            {
                progress.Report("Error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Find file by hash with optimized early termination.
        /// Checks hashes in order of selectivity: CRC -> SHA1 -> MD5
        /// </summary>
        public List<FileEntry> findFile(HashResults hash, bool skipMD5 = false)
        {
            //This should probably return an empty entry rather than null.
            //NOTE: TOSEC and No-Intro use all 3 hashes. MAME and others skips MD5
            
            // Early termination: if CRC doesn't match, no point checking others
            var crcMatches = fileCRCs[hash.crc];
            if (!crcMatches.Any())
                return new List<FileEntry>();
            
            // SHA1 is generally more selective than MD5
            var sha1Matches = fileSHA1s[hash.sha1];
            if (!sha1Matches.Any())
                return new List<FileEntry>();
            
            // Now intersect: files matching both CRC and SHA1
            var allMatches = crcMatches.Intersect(sha1Matches).ToList();
            if (!allMatches.Any())
                return new List<FileEntry>();
            
            // MD5 check only if we have matches and MD5 is not skipped
            if (!skipMD5 && allMatches.Any(a => !string.IsNullOrEmpty(a.hashes.md5)))
            {
                var md5Matches = fileMD5s[hash.md5];
                allMatches = allMatches.Intersect(md5Matches).ToList();
            }
            
            return allMatches;
        }

        /// <summary>
        /// Find disc containing a specific file hash with early termination.
        /// </summary>
        public List<DiscEntry> findDiscs(HashResults hashes)
        {
            //finds all discs with a specified file
            var crcMatches = fileCRCs[hashes.crc];
            if (!crcMatches.Any())
                return new List<DiscEntry>();

            var sha1Matches = fileSHA1s[hashes.sha1];
            if (!sha1Matches.Any())
                return new List<DiscEntry>();

            var matchedFile = crcMatches.Intersect(sha1Matches);
            return matchedFile
                .Where(m => m.parentDisc != null)
                .Select(m => m.parentDisc)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Find disc matching multiple file hashes (multi-file game detection).
        /// Optimized with early termination and reduced LINQ allocations.
        /// </summary>
        public List<DiscEntry> findDisc(List<HashResults> hashes)
        {
            //Starting simple on this: find all references to the
            //NOTE: MAME does not use MD5s, so I MUST be able to find a game where a hash is empty. TOSEC and NOINTRO use all 3 hashes.
            
            //In addition to MAME, we might have a case like SCUMMVM, where there are several languages for a game
            //where MOST of the files across them are identical, but some aren't.  So we need to not bail immediately if we have mulitple matches.

            //Also cases for bin/cue files, where i will expect multiple files for a result and will want to rename them.
            List<DiscEntry> possibleMatches = new List<DiscEntry>();
            
            foreach (var hash in hashes)
            {
                // Early termination: if this file isn't found at all, disc is invalid
                var crcMatches = fileCRCs[hash.crc];
                if (!crcMatches.Any())
                    return new List<DiscEntry>();

                var sha1Matches = fileSHA1s[hash.sha1];
                if (!sha1Matches.Any())
                    return new List<DiscEntry>();

                var matchedFile = crcMatches.Intersect(sha1Matches).ToList();
                if (!matchedFile.Any())
                    return new List<DiscEntry>();

                // Single match: try to validate against full disc
                if (matchedFile.Count == 1)
                {
                    var likelyDisc = matchedFile[0].parentDisc;
                    if (likelyDisc != null && likelyDisc.files.All(f => hashes.Contains(f.hashes)))
                    {
                        return matchedFile
                            .Select(f => f.parentDisc)
                            .Where(d => d != null)
                            .Distinct()
                            .ToList();
                    }
                }
                else
                {
                    // Multiple matches: keep narrowing
                    if (possibleMatches.Any())
                    {
                        // Intersect with previous matches
                        var currentDiscs = matchedFile
                            .Where(m => m.parentDisc != null)
                            .Select(m => m.parentDisc)
                            .Distinct()
                            .ToList();
                        
                        possibleMatches = possibleMatches.Intersect(currentDiscs).ToList();
                        
                        // Early exit if no common discs
                        if (!possibleMatches.Any())
                            return new List<DiscEntry>();
                    }
                    else
                    {
                        possibleMatches = matchedFile
                            .Where(m => m.parentDisc != null)
                            .Select(m => m.parentDisc)
                            .Distinct()
                            .ToList();
                    }
                }
            }

            return possibleMatches;
        }
    }
}

// IDBFS bridge for Unity Data Shards.
//
// Emscripten gives WebGL an in-memory filesystem (MEMFS). Writes land there instantly and are gone
// when the tab closes. IDBFS is the layer that persists a mounted directory into IndexedDB, and it
// only moves data when something calls FS.syncfs — there is no reliable "application quit" in a
// browser to hang that on. Both entry points below are async because syncfs is; each takes a token
// and a function pointer so the C# side can complete the awaiting task.

mergeInto(LibraryManager.library, {

    // Mounts IDBFS at `path`, creating the directories, then pulls whatever IndexedDB already holds
    // into memory. Must complete before anything reads from that path.
    PersistenceIdbfsMount: function (pathPtr, token, callback) {
        var path = UTF8ToString(pathPtr);

        try {
            var segments = path.split('/');
            var walked = '';

            for (var i = 0; i < segments.length; i++) {
                if (segments[i].length === 0) continue;

                walked += '/' + segments[i];

                // mkdir throws EEXIST rather than returning; there is no mkdir -p.
                try { FS.mkdir(walked); } catch (e) { }
            }

            FS.mount(IDBFS, {}, path);
        } catch (e) {
            console.error('[Persistence] IDBFS mount failed for ' + path + ': ' + e);
            {{{ makeDynCall('vii', 'callback') }}}(token, 1);
            return;
        }

        // populate: true — IndexedDB into memory. This is the direction that makes a save survive
        // a page reload.
        FS.syncfs(true, function (error) {
            if (error) console.error('[Persistence] IDBFS populate failed for ' + path + ': ' + error);

            {{{ makeDynCall('vii', 'callback') }}}(token, error ? 1 : 0);
        });
    },

    // Pushes memory into IndexedDB. Called after every write, so a save the caller was told
    // succeeded is durable rather than merely present in RAM.
    PersistenceIdbfsFlush: function (token, callback) {
        FS.syncfs(false, function (error) {
            if (error) console.error('[Persistence] IDBFS flush failed: ' + error);

            {{{ makeDynCall('vii', 'callback') }}}(token, error ? 1 : 0);
        });
    }
});

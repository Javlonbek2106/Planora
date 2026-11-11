// Picks the local database for the platform: SQLite where native code is available (Android, iOS, desktop),
// an in-memory store on the web (the web build is a development preview; SQLite for web needs WebAssembly).
export 'store_factory_memory.dart' if (dart.library.ffi) 'store_factory_sqlite.dart';

use slatedb::admin::{AdminBuilder, CloneSourceSpec};
use slatedb::config::{FlushOptions, FlushType, PutOptions, WriteOptions};
use slatedb::object_store::memory::InMemory;
use slatedb::object_store::ObjectStore;
use slatedb::{Db, DbReader};
use std::sync::Arc;

const NUM_KEYS: u32 = 128;

fn key(i: u32) -> Vec<u8> {
    format!("key-{i:06}").into_bytes()
}

fn value(i: u32) -> Vec<u8> {
    format!("value-{i:06}").into_bytes()
}

async fn read_with_db_reader(
    path: &str,
    object_store: Arc<dyn ObjectStore>,
) -> (u32, Option<String>) {
    let reader = match DbReader::builder(path, object_store).build().await {
        Ok(reader) => reader,
        Err(error) => return (0, Some(format!("DbReader::build failed: {error}"))),
    };

    let mut readable = 0;
    let mut first_error = None;
    for i in 0..NUM_KEYS {
        match reader.get(&key(i)).await {
            Ok(Some(found)) if found.as_ref() == value(i).as_slice() => readable += 1,
            Ok(other) => {
                if first_error.is_none() {
                    first_error = Some(format!("key {i}: unexpected value {other:?}"));
                }
            }
            Err(error) => {
                if first_error.is_none() {
                    first_error = Some(format!("key {i}: {error}"));
                }
            }
        }
    }

    if let Err(error) = reader.close().await {
        if first_error.is_none() {
            first_error = Some(format!("DbReader::close failed: {error}"));
        }
    }
    (readable, first_error)
}

async fn read_with_db(path: &str, object_store: Arc<dyn ObjectStore>) -> (u32, Option<String>) {
    let db = match Db::open(path, object_store).await {
        Ok(db) => db,
        Err(error) => return (0, Some(format!("Db::open failed: {error}"))),
    };

    let mut readable = 0;
    let mut first_error = None;
    for i in 0..NUM_KEYS {
        match db.get(&key(i)).await {
            Ok(Some(found)) if found.as_ref() == value(i).as_slice() => readable += 1,
            Ok(other) => {
                if first_error.is_none() {
                    first_error = Some(format!("key {i}: unexpected value {other:?}"));
                }
            }
            Err(error) => {
                if first_error.is_none() {
                    first_error = Some(format!("key {i}: {error}"));
                }
            }
        }
    }

    if let Err(error) = db.close().await {
        if first_error.is_none() {
            first_error = Some(format!("Db::close failed: {error}"));
        }
    }
    (readable, first_error)
}

#[tokio::main]
async fn main() {
    let target = std::env::var("SLATEDB_PROBE_TARGET").unwrap_or_else(|_| "unknown".to_owned());
    let object_store: Arc<dyn ObjectStore> = Arc::new(InMemory::new());
    let parent_path = "branchcheck-parent";
    let clone_path = "branchcheck-clone";

    let parent = Db::open(parent_path, Arc::clone(&object_store))
        .await
        .expect("open parent");
    let put_options = PutOptions::default();
    let write_options = WriteOptions {
        await_durable: false,
        ..Default::default()
    };
    for i in 0..NUM_KEYS {
        parent
            .put_with_options(&key(i), &value(i), &put_options, &write_options)
            .await
            .expect("put parent key");
    }
    parent.flush().await.expect("flush parent WAL");
    parent
        .flush_with_options(FlushOptions {
            flush_type: FlushType::MemTable,
        })
        .await
        .expect("flush parent memtable to SST");
    parent.close().await.expect("close parent");

    AdminBuilder::new(clone_path, Arc::clone(&object_store))
        .build()
        .create_clone_builder_from_source(CloneSourceSpec::new(parent_path))
        .build()
        .await
        .expect("create zero-copy clone");

    // Three legal observation candidates. The parent reader controls for the API itself;
    // clone Db controls for the clone itself; clone DbReader combines the alternate observer
    // with a parent-resident SST dependency.
    let (parent_reader_count, parent_reader_error) =
        read_with_db_reader(parent_path, Arc::clone(&object_store)).await;
    let (clone_reader_count, clone_reader_error) =
        read_with_db_reader(clone_path, Arc::clone(&object_store)).await;
    let (clone_db_count, clone_db_error) = read_with_db(clone_path, Arc::clone(&object_store)).await;

    println!("version={target}");
    println!("total={NUM_KEYS}");
    println!("parent_reader={parent_reader_count}");
    println!("db={clone_db_count}");
    println!("reader={clone_reader_count}");
    if let Some(error) = parent_reader_error {
        println!("parent_reader_error={}", error.replace(['\r', '\n'], " "));
    }
    if let Some(error) = clone_reader_error {
        println!("reader_error={}", error.replace(['\r', '\n'], " "));
    }
    if let Some(error) = clone_db_error {
        println!("db_error={}", error.replace(['\r', '\n'], " "));
    }
}

use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;

use crate::object::ObjectId;

/// Represents the set of current tips/heads in the causal EventGraph DAG.
/// An event is a head if it has no children in the graph yet.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct Heads {
    heads: BTreeSet<ObjectId>,
}

impl Heads {
    pub fn new() -> Self {
        Self {
            heads: BTreeSet::new(),
        }
    }

    pub fn from_set(heads: BTreeSet<ObjectId>) -> Self {
        Self { heads }
    }

    /// Updates the heads set when a new event is added to the DAG:
    /// 1. Its parents are removed (as they now have a descendant).
    /// 2. The new event is added as a head.
    pub fn apply_event(&mut self, event_id: ObjectId, parents: &[ObjectId]) {
        for parent in parents {
            self.heads.remove(parent);
        }
        self.heads.insert(event_id);
    }

    pub fn contains(&self, id: &ObjectId) -> bool {
        self.heads.contains(id)
    }

    pub fn len(&self) -> usize {
        self.heads.len()
    }

    pub fn is_empty(&self) -> bool {
        self.heads.is_empty()
    }

    pub fn iter(&self) -> impl Iterator<Item = &ObjectId> {
        self.heads.iter()
    }

    pub fn to_vec(&self) -> Vec<ObjectId> {
        self.heads.iter().copied().collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_heads_advancement() {
        let mut heads = Heads::new();
        let e1 = ObjectId::derive(b"TEST", b"event-1");
        let e2 = ObjectId::derive(b"TEST", b"event-2");
        let e3 = ObjectId::derive(b"TEST", b"event-3");

        heads.apply_event(e1, &[]);
        assert_eq!(heads.to_vec(), vec![e1]);

        // e2 depends on e1 -> e1 is retired, e2 is head
        heads.apply_event(e2, &[e1]);
        assert_eq!(heads.to_vec(), vec![e2]);

        // e3 branches from e1 (concurrent branch) -> heads contains e2 and e3
        heads.apply_event(e3, &[e1]);
        assert_eq!(heads.len(), 2);
        assert!(heads.contains(&e2));
        assert!(heads.contains(&e3));

        // e4 merges e2 and e3
        let e4 = ObjectId::derive(b"TEST", b"event-4");
        heads.apply_event(e4, &[e2, e3]);
        assert_eq!(heads.to_vec(), vec![e4]);
    }
}

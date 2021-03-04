//
//  NTAG215Tag+uidSignatures.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/3/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

struct UidSigPair {
    let uid: Data
    let signature: Data
}

extension NTAG215Tag {
    fileprivate static var _uidSignatures: [UidSigPair]? = nil
    static let uidSignatures: [UidSigPair] = {
        if _uidSignatures != nil {
            return _uidSignatures!
        }
        
        var signatures: [UidSigPair] = []
        
        guard
            let signaturesPath = Bundle.main.url(forResource: "signatures", withExtension: "bin"),
            let signaturesData = try? Data(contentsOf: signaturesPath) else {
                return signatures
        }
        
        for index in stride(from: 0, to: signaturesData.count, by: 42) {
            if signaturesData[index + 9] == 0x48 {
                signatures.append(UidSigPair(uid: Data(signaturesData[index..<(index + 9)]), signature: Data(signaturesData[(index + 10)..<(index + 42)])))
            }
        }
        
        if signatures.count == 0 {
            signatures.append(UidSigPair(uid: Data([0x00, 0x00, 0x00, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00]), signature: Data(count: 32)))
        }
        
        _uidSignatures = signatures
        
        return _uidSignatures!
    }()
}

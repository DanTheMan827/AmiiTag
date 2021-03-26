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
    typealias TagSignature = Data
    static var uidSignatures: Dictionary<String, TagSignature> = {
        var signatures: Dictionary<String, Data> = [:]
        
        let folderPath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Signatures")
        let enumerator = FileManager.default.enumerator(atPath: folderPath.path)
        let emptySig = Data(count: 32)
        
        while let element = enumerator?.nextObject() as? String {
            let filePath = folderPath.appendingPathComponent(element)
            if let attributes = try? FileManager.default.attributesOfItem(atPath: filePath.path),
               let size = attributes[FileAttributeKey.size] as? UInt64,
               size == 42,
               let signatureData = try? Data(contentsOf: filePath),
               signatureData[9] == 0x48,
               !signatureData.suffix(32).elementsEqual(emptySig) {
                signatures[signatureData[0..<9].ToHexString()] = Data(signatureData.suffix(32))
            }
        }
        
        guard
            let signaturesPath = Bundle.main.url(forResource: "signatures", withExtension: "bin"),
            let signaturesData = try? Data(contentsOf: signaturesPath) else {
                return signatures
        }
        
        for index in stride(from: 0, to: signaturesData.count, by: 42) {
            if signaturesData[index + 9] == 0x48 {
                signatures[signaturesData[index..<(index + 9)].ToHexString()] = Data(signaturesData[(index + 10)..<(index + 42)])
            }
        }
        
        return signatures
    }()
}
